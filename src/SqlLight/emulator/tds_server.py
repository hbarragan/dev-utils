"""Experimental local TDS endpoint backed by SQLite, not Microsoft SQL Server.

Implements a bounded subset for development. Unsupported requests fail explicitly.
Credentials arrive over stdin, never via command-line arguments or a plaintext file.
"""
import argparse
import datetime
import decimal
import hashlib
import hmac
import json
import logging
import pathlib
import re
import socket
import socketserver
import sqlite3
import struct
import sys
import threading
import uuid

LE16 = lambda n: struct.pack('<H', n)
LE32 = lambda n: struct.pack('<I', n)
LE64 = lambda n: struct.pack('<Q', n)
COLLATION = bytes.fromhex('0904d00034')
LIMIT = 4 * 1024 * 1024
STOP = threading.Event()
CLIENTS = set()
CLIENT_LOCK = threading.Lock()
SLOTS = threading.BoundedSemaphore(16)


def bstr(text):
    return bytes([len(text)]) + text.encode('utf-16le')


def done(count=None, more=False, kind=0xfd, error=False, command=0):
    flags = (1 if more else 0) | (16 if count is not None else 0) | (2 if error else 0)
    return bytes([kind]) + LE16(flags) + LE16(command) + LE64(count or 0)


def error_token(message, number=50000):
    message = message[:1000]
    data = LE32(number) + bytes([1, 16]) + LE16(len(message)) + message.encode('utf-16le') + bstr('SQL Light') + bstr('') + LE32(1)
    return b'\xaa' + LE16(len(data)) + data + done(error=True)


def environment(kind, new, old=''):
    data = bytes([kind]) + bstr(new) + bstr(old)
    return b'\xe3' + LE16(len(data)) + data


def transaction_environment(kind, active):
    descriptor = LE64(1)
    data = bytes([kind]) + (b'\x08' + descriptor + b'\x00' if active else b'\x00\x08' + descriptor)
    return b'\xe3' + LE16(len(data)) + data


def read_exact(sock, size):
    result = bytearray()
    while len(result) < size:
        part = sock.recv(size - len(result))
        if not part:
            raise EOFError()
        result.extend(part)
    return bytes(result)


def receive(sock):
    data = bytearray()
    first_type = None
    while True:
        kind, status, length, _, _, _ = struct.unpack('>BBHHBB', read_exact(sock, 8))
        if length < 8 or first_type is not None and kind != first_type:
            raise ValueError('Invalid packet')
        first_type = kind
        data.extend(read_exact(sock, length - 8))
        if len(data) > LIMIT:
            raise ValueError('Message too large')
        if status & 1:
            return kind, bytes(data)


def send(sock, data):
    for offset in range(0, len(data), 4088):
        part = data[offset:offset + 4088]
        sock.sendall(struct.pack('>BBHHBB', 4, int(offset + len(part) == len(data)), len(part) + 8, 0, 1, 0) + part)


def prelogin(data):
    # ENCRYPT_NOT_SUP: strictly local development with encrypt=false.
    return bytes.fromhex('00000b00060100110001ff') + bytes([15, 0, 0x07, 0xd0, 0, 0, 2])


def login_field(data, offset, password=False):
    start, count = struct.unpack_from('<HH', data, offset)
    raw = data[start:start + count * 2]
    if len(raw) != count * 2:
        raise ValueError('Invalid login')
    if password:
        raw = bytes((((b ^ 0xa5) << 4) & 0xf0) | ((b ^ 0xa5) >> 4) for b in raw)
    return raw.decode('utf-16le')


def skip_headers(data):
    size = struct.unpack_from('<I', data)[0]
    if size < 4 or size > len(data):
        raise ValueError('Invalid ALL_HEADERS')
    return data[size:]


class Reader:
    def __init__(self, data):
        self.data, self.offset = data, 0

    def read(self, count):
        value = self.data[self.offset:self.offset + count]
        if len(value) != count:
            raise ValueError('Truncated RPC')
        self.offset += count
        return value

    def byte(self):
        return self.read(1)[0]

    def ushort(self):
        return struct.unpack('<H', self.read(2))[0]

    def plp(self):
        total = struct.unpack('<Q', self.read(8))[0]
        if total == 0xffffffffffffffff:
            return None
        value = bytearray()
        while True:
            length = struct.unpack('<I', self.read(4))[0]
            if not length:
                return bytes(value)
            if len(value) + length > LIMIT:
                raise ValueError('RPC value too large')
            value.extend(self.read(length))

    def parameter(self):
        name = self.read(self.byte() * 2).decode('utf-16le')
        self.byte()  # input/output flag
        kind = self.byte()
        if kind in (0x26, 0x68, 0x6d, 0x6e, 0x24):
            self.byte()  # maximum length
            raw = self.read(self.byte())
            if not raw:
                value = None
            elif kind == 0x26:
                value = int.from_bytes(raw, 'little', signed=len(raw) != 1)
            elif kind == 0x68:
                value = bool(raw[0])
            elif kind == 0x6d:
                value = struct.unpack('<f' if len(raw) == 4 else '<d', raw)[0]
            elif kind == 0x24:
                value = str(uuid.UUID(bytes_le=raw))
            else:
                raise ValueError('Money RPC parameters are unsupported')
        elif kind in (0xe7, 0xef, 0xa7, 0xaf, 0xa5, 0xad):
            maximum = self.ushort()
            if kind not in (0xa5, 0xad):
                self.read(5)
            if maximum == 65535:
                raw = self.plp()
            else:
                length = self.ushort()
                raw = None if length == 65535 else self.read(length)
            value = raw
            if raw is not None and kind not in (0xa5, 0xad):
                value = raw.decode('utf-16le' if kind in (0xe7, 0xef) else 'cp1252')
        elif kind in (0x6a, 0x6c):
            self.byte(); self.byte(); scale = self.byte()
            raw = self.read(self.byte())
            value = None if not raw else str(decimal.Decimal(int.from_bytes(raw[1:], 'little')) * (1 if raw[0] else -1) / decimal.Decimal(10) ** scale)
        elif kind in (0x28, 0x29, 0x2a):
            scale = self.byte() if kind != 0x28 else 0
            raw = self.read(self.byte())
            value = None
            if raw:
                day = datetime.date(1, 1, 1) + datetime.timedelta(days=int.from_bytes(raw[-3:], 'little')) if kind != 0x29 else None
                if kind == 0x28:
                    value = day.isoformat()
                else:
                    tick_size = 3 if scale <= 2 else 4 if scale <= 4 else 5
                    ticks = int.from_bytes(raw[:tick_size], 'little') / 10 ** scale
                    hour, rest = divmod(ticks, 3600); minute, second = divmod(rest, 60)
                    time = datetime.time(int(hour), int(minute), int(second), int(round((second % 1) * 1_000_000)) % 1_000_000)
                    value = time.isoformat() if kind == 0x29 else datetime.datetime.combine(day, time).isoformat(' ')
        else:
            raise ValueError('Unsupported RPC parameter type 0x%02x' % kind)
        return name, value


def rpc(data):
    reader = Reader(skip_headers(data))
    size = reader.ushort()
    procedure = reader.ushort() if size == 65535 else reader.read(size * 2).decode('utf-16le').lower()
    reader.ushort()
    parameters = []
    while reader.offset < len(reader.data):
        if reader.data[reader.offset] == 0xff:
            raise ValueError('Batched RPC is unsupported; use individual executeUpdate calls')
        parameters.append(reader.parameter())
    return procedure, parameters


def return_handle(handle):
    return b'\xac' + LE16(0) + bstr('@handle') + b'\x01' + LE32(0) + LE16(1) + b'\x26\x04\x04' + struct.pack('<i', handle)


def result_tokens(description, rows):
    data = bytearray(b'\x81' + LE16(len(description)))
    types = []
    for index, col in enumerate(description):
        sample = next((row[index] for row in rows if row[index] is not None), None)
        if isinstance(sample, int):
            kind = 'int'; info = b'\x26\x08'
        elif isinstance(sample, float):
            kind = 'float'; info = b'\x6d\x08'
        elif isinstance(sample, bytes):
            kind = 'binary'; info = b'\xa5' + LE16(8000)
        else:
            kind = 'string'; info = b'\xe7' + LE16(8000) + COLLATION
        types.append(kind)
        name = col[0][:128]
        data.extend(LE32(0) + LE16(1) + info + bstr(name))
    for row in rows:
        data.append(0xd1)
        for kind, value in zip(types, row):
            if kind in ('int', 'float'):
                data.extend(b'\x00' if value is None else b'\x08' + (struct.pack('<q', value) if kind == 'int' else struct.pack('<d', value)))
            else:
                raw = value if kind == 'binary' else str(value).encode('utf-16le')
                if value is None:
                    data.extend(b'\xff\xff')
                elif len(raw) > 8000:
                    raise ValueError('Result value exceeds prototype limit of 8000 bytes')
                else:
                    data.extend(LE16(len(raw)) + raw)
        if len(data) > LIMIT:
            raise ValueError('Result exceeds prototype limit of 4 MB')
    return bytes(data)


def split_sql(sql):
    # Recognize quoted strings/identifiers before splitting, so semicolons in values survive.
    parts, current = [], []
    for match in re.finditer(r"'(?:''|[^'])*'|\[(?:\]\]|[^\]])*\]|\"(?:\"\"|[^\"])*\"|--[^\n]*|/\*[\s\S]*?\*/|.", sql, re.S):
        token = match.group()
        if token.startswith('--') or token.startswith('/*'):
            current.append(' ')
        elif token == ';':
            if ''.join(current).strip(): parts.append(''.join(current).strip())
            current = []
        else:
            current.append(token)
    if ''.join(current).strip(): parts.append(''.join(current).strip())
    return parts


def translate(sql):
    # Only transform outside SQL literals. Unknown syntax remains an explicit SQLite error.
    chunks = re.split(r"('(?:''|[^'])*')", sql)
    for index in range(0, len(chunks), 2):
        text = chunks[index]
        text = re.sub(r'\b(?:dbo|\[dbo\])\s*\.', '', text, flags=re.I)
        text = re.sub(r'\[dbo\]\s*\.', '', text, flags=re.I)
        text = re.sub(r'\bN$', '', text)
        if re.search(r'\bIDENTITY\s*\(', text, re.I):
            raise ValueError('IDENTITY columns are unsupported; use explicit keys')
        text = re.sub(r'\b(?:NVARCHAR|VARCHAR|VARBINARY)\s*\(\s*MAX\s*\)', 'TEXT', text, flags=re.I)
        text = re.sub(r'\bISNULL\s*\(', 'IFNULL(', text, flags=re.I)
        text = re.sub(r'@@IDENTITY', 'last_insert_rowid()', text, flags=re.I)
        text = re.sub(r'\bSCOPE_IDENTITY\s*\(\s*\)', 'last_insert_rowid()', text, flags=re.I)
        text = re.sub(r'\bGETDATE\s*\(\s*\)', 'CURRENT_TIMESTAMP', text, flags=re.I)
        text = re.sub(r'\bNEWID\s*\(\s*\)', 'sql_light_uuid()', text, flags=re.I)
        chunks[index] = text
    translated = ''.join(chunks)
    top = re.match(r'(?is)^SELECT\s+TOP\s*\(?\s*(\d+)\s*\)?\s+(.+)$', translated)
    if top: translated = 'SELECT ' + top[2] + ' LIMIT ' + top[1]
    return translated


class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        if not SLOTS.acquire(blocking=False):
            return
        with CLIENT_LOCK: CLIENTS.add(self.request)
        self.db = None
        self.prepared = {}
        self.implicit = False
        try:
            self.request.settimeout(300)
            kind, data = receive(self.request)
            if kind != 0x12: return
            send(self.request, prelogin(data))
            kind, data = receive(self.request)
            if kind != 0x10 or len(data) < 94: return
            username = login_field(data, 40)
            password = login_field(data, 44, True)
            database = login_field(data, 68)
            config = self.server.config
            if data[25] & 0x80 or not hmac.compare_digest(username.encode(), config['username'].encode()) or not hmac.compare_digest(hashlib.sha256(password.encode()).digest(), self.server.password_hash):
                send(self.request, error_token('Login failed', 18456)); return
            if database.lower() not in ('', config['database'].lower()):
                send(self.request, error_token('Unknown database', 4060)); return
            self.db = sqlite3.connect(self.server.file, isolation_level=None, timeout=5)
            self.db.execute('PRAGMA foreign_keys=ON')
            self.db.execute('PRAGMA journal_mode=WAL')
            self.db.execute('PRAGMA cache_size=-2048')
            self.db.create_function('sql_light_uuid', 0, lambda: str(uuid.uuid4()))
            self.db.create_function('DB_NAME', 0, lambda: config['database'])
            self.db.set_progress_handler(lambda: 1 if STOP.is_set() else 0, 10000)
            ack = b'\x01' + bytes.fromhex('74000004') + bstr('SQL Light SQLite TDS') + bytes([15, 0, 7, 208])
            collation_change = b'\xe3' + LE16(8) + b'\x07\x05' + COLLATION + b'\x00'
            send(self.request, environment(1, config['database'], 'master') + environment(4, '4096', '4096') + collation_change + b'\xad' + LE16(len(ack)) + ack + b'\xae\xff' + done())
            while not STOP.is_set():
                kind, data = receive(self.request)
                try:
                    if kind == 1:
                        response = self.execute(skip_headers(data).decode('utf-16le'))
                    elif kind == 3:
                        response = self.execute_rpc(data)
                    elif kind == 14:
                        response = self.transaction(skip_headers(data))
                    elif kind == 6:
                        response = done()[:1] + LE16(0x20) + done()[3:]
                    else:
                        raise ValueError('Unsupported TDS request %d' % kind)
                except (ValueError, sqlite3.Error) as ex:
                    # Logs do not contain SQL, parameters or credential material.
                    logging.warning('Rejected request: %s', type(ex).__name__)
                    response = error_token('SQL Light experimental: unsupported statement or request. ' + (str(ex) if isinstance(ex, ValueError) else 'SQLite rejected the SQL syntax or constraint.'))
                send(self.request, response)
        except (EOFError, OSError):
            pass
        except Exception as ex:
            logging.warning('Connection ended: %s', type(ex).__name__)
        finally:
            if self.db is not None: self.db.close()  # rollback pending transaction
            with CLIENT_LOCK: CLIENTS.discard(self.request)
            SLOTS.release()

    def execute(self, sql, parameters=None, rpc_call=False):
        output = bytearray()
        sql = re.sub(r'(?is)^\s*(SET IMPLICIT_TRANSACTIONS OFF)\s+(IF @@TRANCOUNT > 0 COMMIT TRAN)\s*$', r'\1;\2', sql)
        statements = split_sql(sql)
        if not statements: return done(kind=0xff if rpc_call else 0xfd)
        for index, statement in enumerate(statements):
            more = index < len(statements) - 1
            kind = 0xff if rpc_call else 0xfd
            upper = statement.upper()
            if re.fullmatch(r'(?is)SELECT\s+compatibility_level\s+FROM\s+sys\.databases\s+WHERE\s+name\s*=\s*db_name\(\)', statement):
                raise ValueError('Hibernate SQL Server dialect detection requires sys.databases compatibility_level. This SQLite TDS prototype does not support that catalog and cannot start aplicaciones Hibernate complejas.')
            if upper.startswith('SET '):
                # Explicit whitelist of session options used by JDBC. No pretend DDL success.
                if not re.fullmatch(r'(?is)SET\s+(?:ANSI_NULLS|ANSI_WARNINGS|ANSI_PADDING|QUOTED_IDENTIFIER|CONCAT_NULL_YIELDS_NULL|ARITHABORT|NUMERIC_ROUNDABORT|NOCOUNT|IMPLICIT_TRANSACTIONS|XACT_ABORT)\s+(?:ON|OFF)', statement) and not re.fullmatch(r'(?is)SET\s+(?:TEXTSIZE|LOCK_TIMEOUT)\s+-?\d+', statement) and not re.fullmatch(r'(?is)SET\s+TRANSACTION\s+ISOLATION\s+LEVEL\s+(?:READ COMMITTED|SERIALIZABLE|READ UNCOMMITTED|REPEATABLE READ)', statement):
                    raise ValueError('Unsupported SET option')
                if upper.startswith('SET IMPLICIT_TRANSACTIONS '):
                    self.implicit = upper.endswith('ON')
                output.extend(done(more=more, kind=kind)); continue
            transaction_command = re.fullmatch(r'(?is)(?:IF\s+@@TRANCOUNT\s*>\s*0\s+)?(COMMIT|ROLLBACK)\s+TRAN(?:SACTION)?', statement)
            if transaction_command:
                if self.db.in_transaction:
                    self.db.commit() if transaction_command[1].upper() == 'COMMIT' else self.db.rollback()
                    output.extend(transaction_environment(9 if transaction_command[1].upper() == 'COMMIT' else 10, False))
                output.extend(done(more=more, kind=kind)); continue
            if re.match(r'(?is)^SELECT\s+@@VERSION\s*$', statement):
                rows, description = [('SQL Light experimental SQLite TDS endpoint',)], [('version',)]
                output.extend(result_tokens(description, rows) + done(len(rows), more, kind)); continue
            if upper.startswith('USE '):
                name = statement[4:].strip().strip('[]"')
                if name.lower() != self.server.config['database'].lower(): raise ValueError('Only the configured database is supported')
                output.extend(done(more=more, kind=kind)); continue
            translated = translate(statement)
            if self.implicit and not self.db.in_transaction:
                self.db.execute('BEGIN')
                output.extend(transaction_environment(8, True))
            cursor = self.db.execute(translated, parameters or {})
            if cursor.description:
                rows = cursor.fetchmany(10001)
                if len(rows) > 10000: raise ValueError('Result exceeds prototype limit of 10000 rows')
                output.extend(result_tokens(cursor.description, rows) + done(len(rows), more, kind))
            else:
                command = 0xc3 if upper.startswith('INSERT') else 0xc5 if upper.startswith('UPDATE') else 0xc4 if upper.startswith('DELETE') else 0xc6
                output.extend(done(max(0, cursor.rowcount), more or rpc_call, kind, command=command))
        return bytes(output)

    def execute_rpc(self, data):
        procedure, parameters = rpc(data)
        values = [value for _, value in parameters]
        handle = None
        if procedure in (10, 'sp_executesql'):
            sql, declarations, args = values[0], values[1] if len(values) > 1 else '', values[2:]
        elif procedure in (13, 'sp_prepexec', 11, 'sp_prepare'):
            sql, declarations, args = values[2], values[1], values[3:]
            handle = max(self.prepared, default=0) + 1
            self.prepared[handle] = (sql, declarations)
            if procedure in (11, 'sp_prepare'):
                return b'\x79' + LE32(0) + return_handle(handle) + done(kind=0xfe)
        elif procedure in (12, 'sp_execute'):
            sql, declarations = self.prepared[values[0]]; args = values[1:]
        elif procedure in (15, 'sp_unprepare'):
            self.prepared.pop(values[0], None)
            return b'\x79' + LE32(0) + done(kind=0xfe)
        elif procedure == 'sp_reset_connection':
            if self.db.in_transaction: self.db.rollback()
            return b'\x79' + LE32(0) + done(kind=0xfe)
        else:
            raise ValueError('Stored procedures and server cursors are unsupported')
        names = re.findall(r'(?:^|,)\s*@([A-Za-z_][A-Za-z0-9_]*)\s+', declarations or '')
        if len(names) != len(args): raise ValueError('Unsupported parameter declaration')
        result = self.execute(sql, dict(zip(names, args)), True)
        return result + b'\x79' + LE32(0) + (return_handle(handle) if handle is not None else b'') + done(kind=0xfe)

    def transaction(self, data):
        code = struct.unpack_from('<H', data)[0]
        if code == 5:
            self.db.execute('BEGIN'); return transaction_environment(8, True) + done()
        if code in (7, 8):
            self.db.commit() if code == 7 else self.db.rollback()
            return transaction_environment(9 if code == 7 else 10, False) + done()
        raise ValueError('Unsupported transaction request')


class Server(socketserver.ThreadingTCPServer):
    daemon_threads = False
    allow_reuse_address = False


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--folder', required=True)
    parser.add_argument('--port', type=int, required=True)
    args = parser.parse_args()
    folder = pathlib.Path(args.folder).resolve()
    folder.mkdir(parents=True, exist_ok=True)
    logging.basicConfig(filename=folder / 'emulator.log', level=logging.INFO, format='%(asctime)s %(message)s')
    config = json.loads(sys.stdin.readline())
    secret = config.pop('password')
    stop_file = folder / 'stop.request'
    stop_file.unlink(missing_ok=True)
    with Server(('127.0.0.1', args.port), Handler) as server:
        server.config = config
        server.password_hash = hashlib.sha256(secret.encode()).digest()
        del secret
        server.file = str(folder / 'database.sqlite')
        def watch():
            while not STOP.wait(0.25):
                if stop_file.exists():
                    STOP.set()
                    with CLIENT_LOCK:
                        for client in tuple(CLIENTS):
                            try: client.shutdown(socket.SHUT_RDWR)
                            except OSError: pass
                    server.shutdown()
                    return
        threading.Thread(target=watch, daemon=True).start()
        logging.info('Experimental SQLite TDS endpoint listening on loopback port %d', args.port)
        server.serve_forever(poll_interval=0.25)
    stop_file.unlink(missing_ok=True)
    logging.info('Stopped')


if __name__ == '__main__':
    main()
