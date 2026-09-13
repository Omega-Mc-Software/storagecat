using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StorageCat;

public class Unit
{
    public long Id;
    public string Name = "";
    public string Size = "";
    public decimal Rent;
    public string Tenant = "";
    public string Phone = "";
    public string MoveIn = "";
    public string Notes = "";
    public bool Occupied => !string.IsNullOrWhiteSpace(Tenant);
}

public class Txn
{
    public long Id;
    public string Date = "";
    public long? UnitId;
    public string UnitLabel = "";
    public string Kind = "expense"; // rent | late | hold | expense
    public string Category = "";
    public decimal Amount;
    public string Note = "";
}

public class WaitEntry
{
    public long Id;
    public string Created = "";
    public string Name = "";
    public string Phone = "";
    public string SizeWanted = "";
    public bool HoldPaid;
    public string Notes = "";
}

public class SizeRow
{
    public string Name = "";
    public int Qty;
    public decimal Price;
}

public static class Db
{
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StorageCat");
    public static string DbPath => Path.Combine(DataDir, "storagecat.db");

    static SqliteConnection Open()
    {
        var c = new SqliteConnection("Data Source=" + DbPath);
        c.Open();
        return c;
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS units(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL, size TEXT DEFAULT '',
  rent REAL DEFAULT 0, tenant TEXT DEFAULT '', phone TEXT DEFAULT '',
  move_in TEXT DEFAULT '', notes TEXT DEFAULT '', deleted INTEGER DEFAULT 0);
CREATE TABLE IF NOT EXISTS transactions(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  date TEXT NOT NULL, unit_id INTEGER,
  kind TEXT NOT NULL, category TEXT DEFAULT '', amount REAL NOT NULL, note TEXT DEFAULT '', deleted INTEGER DEFAULT 0);
CREATE TABLE IF NOT EXISTS waitlist(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  created TEXT NOT NULL, name TEXT NOT NULL, phone TEXT DEFAULT '',
  size_wanted TEXT DEFAULT '', hold_paid INTEGER DEFAULT 0, notes TEXT DEFAULT '', deleted INTEGER DEFAULT 0);
CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        // v0.1.0 databases predate soft delete — add the columns if missing
        foreach (var table in new[] { "units", "transactions", "waitlist" })
        {
            using var mig = con.CreateCommand();
            mig.CommandText = $"ALTER TABLE {table} ADD COLUMN deleted INTEGER DEFAULT 0";
            try { mig.ExecuteNonQuery(); } catch { /* column already exists */ }
        }
    }

    // ---------- settings ----------

    public static string GetSetting(string key, string def = "")
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        var r = cmd.ExecuteScalar();
        return r == null || r is DBNull ? def : r.ToString() ?? def;
    }

    public static void SetSetting(string key, string value)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public static int GetIntSetting(string key, int def)
    {
        return int.TryParse(GetSetting(key, ""), out var v) ? v : def;
    }

    public static decimal GetDecSetting(string key, decimal def)
    {
        return decimal.TryParse(GetSetting(key, ""), out var v) ? v : def;
    }

    static readonly JsonSerializerOptions SizeOpts = new() { IncludeFields = true };

    public static List<SizeRow> GetSizes()
    {
        try
        {
            return JsonSerializer.Deserialize<List<SizeRow>>(GetSetting("sizes", "[]"), SizeOpts) ?? new();
        }
        catch { return new(); }
    }

    public static void SaveSizes(List<SizeRow> rows)
        => SetSetting("sizes", JsonSerializer.Serialize(rows, SizeOpts));

    static object Num(decimal d) => (double)d;
    static object Id(long? id) => id.HasValue ? id.Value : DBNull.Value;
    static decimal ToDec(object? o) => o == null || o is DBNull ? 0m : Convert.ToDecimal(o);
    static int ToInt(object? o) => o == null || o is DBNull ? 0 : Convert.ToInt32(o);

    // ---------- units ----------

    public static List<Unit> ListUnits(bool includeDeleted = false)
    {
        var list = new List<Unit>();
        using var con = Open();
        using var cmd = con.CreateCommand();
        // Deleted view shows ONLY deleted rows — includeDeleted=true used to drop
        // the WHERE entirely and show everything (Dad, v0.1.1 test)
        cmd.CommandText = "SELECT id,name,size,rent,tenant,phone,move_in,notes FROM units" +
            (includeDeleted ? " WHERE deleted=1" : " WHERE deleted=0") + " ORDER BY name COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Unit
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                Size = r.GetString(2),
                Rent = ToDec(r[3]),
                Tenant = r.GetString(4),
                Phone = r.GetString(5),
                MoveIn = r.IsDBNull(6) ? "" : r.GetString(6),
                Notes = r.GetString(7),
            });
        return list;
    }

    public static long SaveUnit(Unit u)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        if (u.Id == 0)
        {
            cmd.CommandText = @"INSERT INTO units(name,size,rent,tenant,phone,move_in,notes)
                VALUES($n,$s,$r,$t,$p,$m,$o); SELECT last_insert_rowid();";
        }
        else
        {
            cmd.CommandText = @"UPDATE units SET name=$n, size=$s, rent=$r, tenant=$t, phone=$p, move_in=$m, notes=$o
                WHERE id=$id; SELECT $id;";
            cmd.Parameters.AddWithValue("$id", u.Id);
        }
        cmd.Parameters.AddWithValue("$n", u.Name);
        cmd.Parameters.AddWithValue("$s", u.Size);
        cmd.Parameters.AddWithValue("$r", Num(u.Rent));
        cmd.Parameters.AddWithValue("$t", u.Tenant);
        cmd.Parameters.AddWithValue("$p", u.Phone);
        cmd.Parameters.AddWithValue("$m", u.MoveIn);
        cmd.Parameters.AddWithValue("$o", u.Notes);
        var v = cmd.ExecuteScalar();
        return Convert.ToInt64(v ?? 0L);
    }

    /// Soft delete: row stays in the database, can be undone from the Deleted view.
    public static void SoftDeleteUnit(long id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE units SET deleted=1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static void RestoreUnit(long id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE units SET deleted=0 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// Hard delete: past transactions are kept but lose the unit link.
    public static void DeleteUnit(long id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
UPDATE transactions SET unit_id=NULL WHERE unit_id=$id;
DELETE FROM units WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---------- transactions ----------

    public static List<Txn> ListTxns(bool includeDeleted = false)
    {
        var list = new List<Txn>();
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT t.id, t.date, t.unit_id,
  CASE WHEN u.id IS NULL THEN '' ELSE u.name END,
  t.kind, t.category, t.amount, t.note
FROM transactions t LEFT JOIN units u ON u.id = t.unit_id" +
    (includeDeleted ? " WHERE t.deleted=1" : " WHERE t.deleted=0") + @"
ORDER BY t.date DESC, t.id DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Txn
            {
                Id = r.GetInt64(0),
                Date = r.GetString(1),
                UnitId = r.IsDBNull(2) ? null : r.GetInt64(2),
                UnitLabel = r.IsDBNull(3) ? "" : r.GetString(3),
                Kind = r.GetString(4),
                Category = r.GetString(5),
                Amount = ToDec(r[6]),
                Note = r.GetString(7),
            });
        return list;
    }

    public static void SaveTxn(Txn t)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        if (t.Id == 0)
            cmd.CommandText = @"INSERT INTO transactions(date,unit_id,kind,category,amount,note)
                VALUES($d,$u,$k,$c,$a,$n)";
        else
        {
            cmd.CommandText = @"UPDATE transactions SET date=$d, unit_id=$u, kind=$k, category=$c, amount=$a, note=$n
                WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", t.Id);
        }
        cmd.Parameters.AddWithValue("$d", t.Date);
        cmd.Parameters.AddWithValue("$u", Id(t.UnitId));
        cmd.Parameters.AddWithValue("$k", t.Kind);
        cmd.Parameters.AddWithValue("$c", t.Category);
        cmd.Parameters.AddWithValue("$a", Num(t.Amount));
        cmd.Parameters.AddWithValue("$n", t.Note);
        cmd.ExecuteNonQuery();
    }

    public static void SoftDeleteTxn(long id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE transactions SET deleted=1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static void RestoreTxn(long id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE transactions SET deleted=0 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static void DeleteTxn(long id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM transactions WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---------- waitlist ----------

    public static List<WaitEntry> ListWait(bool includeDeleted = false)
    {
        var list = new List<WaitEntry>();
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id,created,name,phone,size_wanted,hold_paid,notes FROM waitlist" +
            (includeDeleted ? " WHERE deleted=1" : " WHERE deleted=0") + " ORDER BY created ASC, id ASC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new WaitEntry
            {
                Id = r.GetInt64(0),
                Created = r.GetString(1),
                Name = r.GetString(2),
                Phone = r.GetString(3),
                SizeWanted = r.GetString(4),
                HoldPaid = ToInt(r[5]) != 0,
                Notes = r.GetString(6),
            });
        return list;
    }

    public static long SaveWait(WaitEntry w)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        if (w.Id == 0)
            cmd.CommandText = @"INSERT INTO waitlist(created,name,phone,size_wanted,hold_paid,notes)
                VALUES($d,$n,$p,$s,$h,$o)";
        else
        {
            cmd.CommandText = @"UPDATE waitlist SET created=$d, name=$n, phone=$p, size_wanted=$s, hold_paid=$h, notes=$o
                WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", w.Id);
        }
        cmd.Parameters.AddWithValue("$d", w.Created);
        cmd.Parameters.AddWithValue("$n", w.Name);
        cmd.Parameters.AddWithValue("$p", w.Phone);
        cmd.Parameters.AddWithValue("$s", w.SizeWanted);
        cmd.Parameters.AddWithValue("$h", w.HoldPaid ? 1 : 0);
        cmd.Parameters.AddWithValue("$o", w.Notes);
        var v = w.Id == 0 ? cmd.ExecuteScalar() : (object)w.Id;
        return Convert.ToInt64(v ?? 0L);
    }

    public static void SetWaitHoldPaid(long id, bool paid)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE waitlist SET hold_paid=$h WHERE id=$id";
        cmd.Parameters.AddWithValue("$h", paid ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static void SoftDeleteWait(long id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE waitlist SET deleted=1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static void RestoreWait(long id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE waitlist SET deleted=0 WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static void DeleteWait(long id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM waitlist WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---------- backup ----------

    public static void WipeAll()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM transactions; DELETE FROM waitlist; DELETE FROM units;";
        cmd.ExecuteNonQuery();
    }

    // ---------- deleted counts (for tab hints) ----------

    public static int CountDeleted(string table)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE deleted=1";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }
}
