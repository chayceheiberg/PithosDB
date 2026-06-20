using System.Text;
using PithosDB.Core;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: pithosdb-shell <database-path>");
    return 1;
}

string dbPath = args[0];

PrintBanner(dbPath);

using var db = new PithosDb(dbPath, new PithosOptions { EnableTtl = true });

while (true)
{
    Console.Write("pithos> ");
    string? line = Console.ReadLine();

    if (line is null) break; // EOF (Ctrl+D / piped input)

    line = line.TrimStart('﻿').Trim();
    if (line.Length == 0) continue;

    var (cmd, rest) = Split(line);

    switch (cmd.ToLowerInvariant())
    {
        case "put":
        {
            var (key, rest2) = Split(rest);
            var (value, ttlStr) = Split(rest2);
            if (key.Length == 0 || value.Length == 0)
            {
                Err("Usage: put <key> <value> [ttl-seconds]");
                break;
            }
            if (ttlStr.Length > 0 && double.TryParse(ttlStr, out double secs))
            {
                db.Put(Encode(key), Encode(value), TimeSpan.FromSeconds(secs));
                Console.WriteLine($"OK (expires in {secs}s)");
            }
            else
            {
                db.Put(Encode(key), Encode(value));
                Console.WriteLine("OK");
            }
            break;
        }

        case "get":
        {
            if (rest.Length == 0) { Err("Usage: get <key>"); break; }
            if (db.TryGet(Encode(rest), out var value))
                Console.WriteLine(value is null ? "(tombstone)" : Decode(value));
            else
                Console.WriteLine("(not found)");
            break;
        }

        case "delete":
        case "del":
        {
            if (rest.Length == 0) { Err("Usage: delete <key>"); break; }
            db.Delete(Encode(rest));
            Console.WriteLine("OK");
            break;
        }

        case "scan":
        {
            // scan [<from> [<to>]]
            var (from, to) = Split(rest);
            byte[]? fromKey = from.Length > 0 ? Encode(from) : null;
            byte[]? toKey   = to.Length   > 0 ? Encode(to)   : null;

            int count = 0;
            foreach (var (k, v) in db.Scan(fromKey, toKey))
            {
                Console.WriteLine($"{Decode(k)} = {Decode(v)}");
                count++;
            }
            Console.WriteLine($"({count} {(count == 1 ? "entry" : "entries")})");
            break;
        }

        case "help":
            Console.WriteLine("""
                Commands:
                  put <key> <value> [ttl-seconds]   Insert or update a key (optional TTL)
                  get <key>                         Retrieve a value
                  delete <key>                      Delete a key
                  scan [<from> [<to>]]              List entries in key range (inclusive, omit for full scan)
                  help                              Show this message
                  exit                              Quit
                """);
            break;

        case "exit":
        case "quit":
            return 0;

        default:
            Err($"Unknown command '{cmd}'. Type 'help' for available commands.");
            break;
    }
}

return 0;

static void PrintBanner(string dbPath)
{
    static void Jar(string s) { Console.ForegroundColor = ConsoleColor.DarkYellow; Console.WriteLine(s); }

    Jar("         _____      ");
    Jar("       ,'     ',    ");
    Jar("      /         \\  ");
    Jar("     | . . . . . | ");
    Console.ForegroundColor = ConsoleColor.DarkYellow;
    Console.Write("     |  ");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("PITHOS");
    Console.ForegroundColor = ConsoleColor.DarkYellow;
    Console.WriteLine("   | ");
    Jar("     | . . . . . | ");
    Jar("     |           | ");
    Jar("     |  ~     ~  | ");
    Jar("     |           | ");
    Jar("      \\         /  ");
    Jar("       '-_____-'   ");
    Jar("           |       ");
    Jar("       ____|____   ");
    Jar("      |_________|  ");
    Console.ResetColor();
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"  db: {dbPath}");
    Console.ResetColor();
    Console.WriteLine("  Type 'help' for available commands.");
    Console.WriteLine();
}

static (string first, string rest) Split(string s)
{
    s = s.TrimStart();
    int i = s.IndexOf(' ');
    return i < 0 ? (s, "") : (s[..i], s[(i + 1)..].TrimStart());
}

static byte[] Encode(string s) => Encoding.UTF8.GetBytes(s);
static string  Decode(byte[] b) => Encoding.UTF8.GetString(b);
static void    Err(string msg)  => Console.Error.WriteLine($"error: {msg}");
