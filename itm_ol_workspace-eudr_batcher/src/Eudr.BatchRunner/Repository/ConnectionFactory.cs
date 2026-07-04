using System.Data;
using FirebirdSql.Data.FirebirdClient;

namespace Eudr.BatchRunner.Repository;

public interface IConnectionFactory
{
    IDbConnection Create();
}

public sealed class FirebirdConnectionFactory(string connectionString) : IConnectionFactory
{
    public IDbConnection Create()
    {
        var conn = new FbConnection(connectionString);
        conn.Open();
        return conn;
    }
}
