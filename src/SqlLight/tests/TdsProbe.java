import java.sql.*;

class TdsProbe {
    static void check(boolean condition, String message) { if (!condition) throw new AssertionError(message); }
    public static void main(String[] args) throws Exception {
        Class.forName("com.microsoft.sqlserver.jdbc.SQLServerDriver");
        String password = System.getenv("SQL_LIGHT_TEST_PASSWORD");
        try (Connection db = DriverManager.getConnection(args[0], "user", password)) {
            try (Statement stmt = db.createStatement()) {
                if (args.length > 1 && args[1].equals("read")) {
                    try (ResultSet rows = stmt.executeQuery("SELECT value FROM persistence_check WHERE id=1")) {
                        check(rows.next() && rows.getString(1).equals("persistente ñ; @P0"), "persistence lost");
                    }
                    System.out.println("TDS JDBC persistence OK"); return;
                }
                stmt.executeUpdate("CREATE TABLE persistence_check (id int PRIMARY KEY, value nvarchar(200), flag int)");
            }
            try (PreparedStatement stmt = db.prepareStatement("INSERT INTO persistence_check(id,value,flag) VALUES (?,?,?)")) {
                stmt.setInt(1,1); stmt.setString(2,"persistente ñ; @P0"); stmt.setNull(3, Types.INTEGER);
                check(stmt.executeUpdate()==1,"insert count");
                stmt.setInt(1,2); stmt.setString(2,"second"); stmt.setInt(3,1);
                check(stmt.executeUpdate()==1,"prepared handle reuse");
            }
            try (PreparedStatement stmt = db.prepareStatement("SELECT value,flag FROM dbo.persistence_check WHERE id=?")) {
                stmt.setInt(1,1);
                try (ResultSet rows = stmt.executeQuery()) {
                    check(rows.next() && rows.getString(1).equals("persistente ñ; @P0"),"parameter/unicode mismatch");
                    check(rows.getObject(2)==null,"NULL result mismatch");
                }
            }
            db.setAutoCommit(false);
            try (PreparedStatement stmt = db.prepareStatement("UPDATE persistence_check SET value=? WHERE id=?")) {
                stmt.setString(1,"rollback"); stmt.setInt(2,1); stmt.executeUpdate();
            }
            db.rollback(); db.setAutoCommit(true);
            try (Statement stmt = db.createStatement(); ResultSet rows = stmt.executeQuery("SELECT TOP 1 value FROM persistence_check WHERE id=1")) {
                check(rows.next() && rows.getString(1).equals("persistente ñ; @P0"),"rollback failed");
            }
            try (PreparedStatement stmt = db.prepareStatement("DELETE FROM persistence_check WHERE id=?")) {
                stmt.setInt(1,2); check(stmt.executeUpdate()==1,"delete count");
            }
            try (Statement stmt = db.createStatement()) {
                try { stmt.executeQuery("SELECT compatibility_level FROM sys.databases where name = db_name();"); throw new AssertionError("unsupported Hibernate catalog accepted"); }
                catch (SQLException expected) { check(expected.getMessage().contains("cannot start aplicaciones Hibernate complejas"),"missing Hibernate compatibility diagnostic"); }
                try { stmt.execute("CREATE PROCEDURE fake AS SELECT 1"); throw new AssertionError("unsupported SQL accepted"); }
                catch (SQLException expected) { check(expected.getMessage().contains("experimental"),"unsupported SQL error"); }
                try { stmt.execute("CREATE TABLE identity_not_supported (id int IDENTITY(1,1) PRIMARY KEY)"); throw new AssertionError("IDENTITY silently accepted"); }
                catch (SQLException expected) { check(expected.getMessage().contains("IDENTITY"),"IDENTITY error"); }
            }
        }
        try { DriverManager.getConnection(args[0],"user","incorrect"); throw new AssertionError("wrong password accepted"); }
        catch (SQLException expected) { check(expected.getErrorCode()==18456,"wrong auth error"); }
        System.out.println("TDS JDBC CRUD, prepared statements, Unicode, NULL, rollback and authentication OK");
    }
}
