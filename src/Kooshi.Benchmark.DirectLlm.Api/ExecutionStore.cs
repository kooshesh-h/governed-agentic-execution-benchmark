using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kooshi.Benchmark.DirectLlm.Api;

public sealed class ExecutionStore : Kooshi.Benchmark.Shared.IBookingCommitStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ExecutionStore(IConfiguration configuration)
    {
        var path = configuration["DirectLlm:DatabasePath"] ?? "directllm.db";
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath }.ToString();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS executions (id TEXT PRIMARY KEY, state TEXT NOT NULL); CREATE TABLE IF NOT EXISTS bookings (execution_id TEXT PRIMARY KEY, booking_id TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    public async Task<ExecutionState?> GetAsync(Guid id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT state FROM executions WHERE id = $id";
            command.Parameters.AddWithValue("$id", id.ToString());
            var json = (string?)await command.ExecuteScalarAsync(ct);
            return json is null ? null : JsonSerializer.Deserialize<ExecutionState>(json, JsonOptions);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ExecutionState state, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO executions(id,state) VALUES($id,$state) ON CONFLICT(id) DO UPDATE SET state=$state";
            command.Parameters.AddWithValue("$id", state.Id.ToString());
            command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state, JsonOptions));
            await command.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<(string BookingId, bool Duplicate)> CommitBookingAsync(Guid executionId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO bookings(execution_id,booking_id) VALUES($id,$booking)";
            command.Parameters.AddWithValue("$id", executionId.ToString());
            command.Parameters.AddWithValue("$booking", Guid.NewGuid().ToString());
            var affected = await command.ExecuteNonQueryAsync(ct);
            command.CommandText = "SELECT booking_id FROM bookings WHERE execution_id=$id";
            var bookingId = (string)(await command.ExecuteScalarAsync(ct))!;
            return (bookingId, affected == 0);
        }
        finally { _gate.Release(); }
    }
}



