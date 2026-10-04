import java.sql.*;
import java.nio.file.*;

// Fail fast like sqlcmd -b. Never prints SQL batches or credential material.
class TdsScriptProbe {
    public static void main(String[] args) throws Exception {
        Class.forName("com.microsoft.sqlserver.jdbc.SQLServerDriver");
        String[] scripts = {
            "0.0.0.0/01-remove-db.sql", "2.0.0.0/01-create-db.sql",
            "2.0.0.0/02-schema-create.sql", "2.0.0.0/03-schema-migration.sql", "2.0.0.0/04-staging-tables.sql",
            "2.1.0.0/01-alter-table-logbook.sql", "2.1.0.0/02-create-exception-data.sql",
            "2.1.0.0/03-alter-table-job-status-detail.sql", "2.1.0.0/04-drop-staging-tables.sql",
            "2.1.0.0/05-update-properties-column.sql", "2.1.0.0/06-add-report-reference-columns.sql",
            "2.1.0.0/07-add-batch-attachment-column.sql"
        };
        String password = System.getenv("SQL_LIGHT_TEST_PASSWORD");
        try (Connection db = DriverManager.getConnection(args[0], args.length > 2 ? args[2] : "user", password); Statement statement = db.createStatement()) {
            statement.setQueryTimeout(10);
            System.out.println("JDBC login: OK");
            for (String script : scripts) {
                String text = Files.readString(Path.of(args[1], script));
                String[] batches = text.split("(?im)^\\s*GO\\s*(?:--[^\\r\\n]*)?$");
                int batch = 0;
                for (String sql : batches) {
                    batch++;
                    if (sql.isBlank()) continue;
                    try { statement.execute(sql); }
                    catch (SQLException error) {
                        System.out.println("INITIALIZATION FAILED: " + script + " batch " + batch + " SQL error " + error.getErrorCode());
                        System.out.println(error.getMessage().replace(password, "[hidden]"));
                        System.exit(2);
                    }
                }
                System.out.println("SCRIPT OK: " + script);
            }
        }
    }
}
