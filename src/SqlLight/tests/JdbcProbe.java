import java.sql.*;

class JdbcProbe {
    public static void main(String[] args) throws Exception {
        Class.forName(args[0]);
        try (Connection db = DriverManager.getConnection(args[1], "user", System.getenv("SQL_LIGHT_TEST_PASSWORD"));
             Statement statement = db.createStatement();
             ResultSet rows = statement.executeQuery("SELECT value FROM persistence_check WHERE id = 1")) {
            if (!rows.next() || !rows.getString(1).equals("still here")) throw new AssertionError("Missing persistent row");
            System.out.println("JDBC OK: " + db.getMetaData().getDatabaseProductName());
        }
    }
}
