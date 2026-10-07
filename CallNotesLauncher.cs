// CallNotes is kept in one source file so it builds with the C# compiler
// included on Windows, without package restore or third-party dependencies.
//
// Source map:
//   CallRecord / NoteBuffer  - call data and efficient note editing
//   JsonReader / CallHistory - local JSON loading and validation
//   CallWriter               - streaming history saves
//   CallNotesApp              - terminal UI, keyboard input, settings, search
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// Persistent shape of one call. Notes are kept in a NoteBuffer while editing
// and copied back into this record when a save snapshot is written.
internal sealed class CallRecord
{
    public int Id;
    public string StartTime = "";
    public string EndTime = "";
    public string CallerName = "";
    public string Location = "";
    public string Number = "";
    public string Inc = "";
    public string Type = "";
    public string Notes = "";
    public string Status = "";
}

// Piece-table editor for call notes. Existing text stays in one immutable
// string; edits add small pieces instead of rebuilding a potentially huge note.
internal sealed class NoteBuffer
{
    internal struct Piece
    {
        public bool Added;
        public int Start;
        public int Length;
        public Piece(bool added, int start, int length) { Added = added; Start = start; Length = length; }
    }

    private readonly string original;
    private readonly StringBuilder additions = new StringBuilder();
    private readonly List<Piece> pieces = new List<Piece>();
    public int Length { get; private set; }

    public NoteBuffer(string text)
    {
        original = text ?? "";
        Length = original.Length;
        if (Length > 0) pieces.Add(new Piece(false, 0, Length));
    }

    public void Insert(int index, string text)
    {
        if (String.IsNullOrEmpty(text)) return;
        index = Math.Max(0, Math.Min(index, Length));
        int start = additions.Length;
        additions.Append(text);
        InsertPiece(index, new Piece(true, start, text.Length));
        Length += text.Length;
    }

    public void Remove(int index, int count)
    {
        if (count <= 0 || Length == 0) return;
        index = Math.Max(0, Math.Min(index, Length));
        count = Math.Min(count, Length - index);
        SplitAt(index + count);
        int first = SplitAt(index);
        int last = SplitAt(index + count);
        if (last > first) pieces.RemoveRange(first, last - first);
        Length -= count;
        Coalesce();
    }

    public string GetRange(int index, int count)
    {
        index = Math.Max(0, Math.Min(index, Length));
        count = Math.Max(0, Math.Min(count, Length - index));
        StringBuilder result = new StringBuilder(count);
        int skip = index;
        int remaining = count;
        foreach (Piece piece in pieces)
        {
            if (skip >= piece.Length) { skip -= piece.Length; continue; }
            int take = Math.Min(remaining, piece.Length - skip);
            if (piece.Added)
            {
                for (int i = 0; i < take; i++) result.Append(additions[piece.Start + skip + i]);
            }
            else result.Append(original, piece.Start + skip, take);
            remaining -= take;
            skip = 0;
            if (remaining == 0) break;
        }
        return result.ToString();
    }

    public char GetCharAt(int index)
    {
        if (index < 0 || index >= Length) throw new ArgumentOutOfRangeException("index");
        int offset = 0;
        foreach (Piece piece in pieces)
        {
            if (index < offset + piece.Length)
                return piece.Added ? additions[piece.Start + index - offset] : original[piece.Start + index - offset];
            offset += piece.Length;
        }
        throw new ArgumentOutOfRangeException("index");
    }

    public int WordBoundary(int index, bool backward)
    {
        int position = Math.Max(0, Math.Min(index, Length));
        if (backward)
        {
            while (position > 0 && Char.IsWhiteSpace(GetCharAt(position - 1))) position--;
            while (position > 0 && !Char.IsWhiteSpace(GetCharAt(position - 1))) position--;
        }
        else
        {
            while (position < Length && !Char.IsWhiteSpace(GetCharAt(position))) position++;
            while (position < Length && Char.IsWhiteSpace(GetCharAt(position))) position++;
        }
        return position;
    }

    public int IndexOf(string value, int startIndex, StringComparison comparison)
    {
        if (String.IsNullOrEmpty(value)) return Math.Max(0, Math.Min(startIndex, Length));
        // Keep each temporary search string bounded. The overlap catches a
        // match that begins at the end of one chunk and finishes in the next.
        int overlap = value.Length - 1;
        int position = Math.Max(0, Math.Min(startIndex, Length));
        while (position < Length)
        {
            int count = Math.Min(32768 + overlap, Length - position);
            string chunk = GetRange(position, count);
            int found = chunk.IndexOf(value, comparison);
            if (found >= 0) return position + found;
            if (count <= overlap) break;
            position += count - overlap;
        }
        return -1;
    }

    public Snapshot GetSnapshot()
    {
        return new Snapshot(original, additions.ToString(), pieces.ToArray());
    }

    private void InsertPiece(int index, Piece added)
    {
        int at = SplitAt(index);
        pieces.Insert(at, added);
        Coalesce();
    }

    private int SplitAt(int index)
    {
        if (index <= 0) return 0;
        int position = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            Piece piece = pieces[i];
            if (index == position) return i;
            if (index == position + piece.Length) return i + 1;
            if (index < position + piece.Length)
            {
                int leftLength = index - position;
                pieces[i] = new Piece(piece.Added, piece.Start, leftLength);
                pieces.Insert(i + 1, new Piece(piece.Added, piece.Start + leftLength, piece.Length - leftLength));
                return i + 1;
            }
            position += piece.Length;
        }
        return pieces.Count;
    }

    private void Coalesce()
    {
        for (int i = pieces.Count - 1; i > 0; i--)
        {
            Piece left = pieces[i - 1], right = pieces[i];
            if (left.Added == right.Added && left.Start + left.Length == right.Start)
            {
                pieces[i - 1] = new Piece(left.Added, left.Start, left.Length + right.Length);
                pieces.RemoveAt(i);
            }
        }
    }

    internal sealed class Snapshot
    {
        private readonly string original;
        private readonly string additions;
        private readonly Piece[] pieces;
        internal Snapshot(string source, string added, Piece[] spans)
        { original = source; additions = added; pieces = spans; }

        public void WriteJson(TextWriter writer)
        {
            writer.Write('"');
            char[] chunk = new char[65536];
            foreach (Piece piece in pieces)
            {
                string source = piece.Added ? additions : original;
                int offset = piece.Start, remaining = piece.Length;
                while (remaining > 0)
                {
                    int size = Math.Min(remaining, chunk.Length);
                    source.CopyTo(offset, chunk, 0, size);
                    JsonOutput.WriteEscaped(writer, chunk, size);
                    offset += size;
                    remaining -= size;
                }
            }
            writer.Write('"');
        }
    }
}

// JSON encoding and decoding are implemented locally to avoid external
// dependencies and to support the .NET Framework runtime available on Windows.
internal static class JsonOutput
{
    public static void WriteString(TextWriter writer, string value)
    {
        if (value == null) { writer.Write("null"); return; }
        writer.Write('"');
        char[] chunk = new char[65536];
        int offset = 0;
        while (offset < value.Length)
        {
            int size = Math.Min(chunk.Length, value.Length - offset);
            value.CopyTo(offset, chunk, 0, size);
            WriteEscaped(writer, chunk, size);
            offset += size;
        }
        writer.Write('"');
    }

    public static void WriteEscaped(TextWriter writer, char[] value, int length)
    {
        int run = 0;
        for (int i = 0; i < length; i++)
        {
            char c = value[i];
            if (c >= ' ' && c != '"' && c != '\\') continue;
            if (i > run) writer.Write(value, run, i - run);
            switch (c)
            {
                case '"': writer.Write("\\\""); break;
                case '\\': writer.Write("\\\\"); break;
                case '\b': writer.Write("\\b"); break;
                case '\f': writer.Write("\\f"); break;
                case '\n': writer.Write("\\n"); break;
                case '\r': writer.Write("\\r"); break;
                case '\t': writer.Write("\\t"); break;
                default:
                    writer.Write("\\u");
                    writer.Write(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
            }
            run = i + 1;
        }
        if (run < length) writer.Write(value, run, length - run);
    }
}

// Streaming parser used for loading and validating the local call-history JSON.
internal sealed class JsonReader
{
    private readonly TextReader reader;
    private readonly char[] buffer = new char[65536];
    private int offset, length;

    public JsonReader(TextReader input) { reader = input; }
    public int Peek()
    {
        if (offset >= length)
        {
            length = reader.Read(buffer, 0, buffer.Length);
            offset = 0;
            if (length == 0) return -1;
        }
        return buffer[offset];
    }
    public int Read() { int c = Peek(); if (c >= 0) offset++; return c; }
    public void White() { while (Peek() == ' ' || Peek() == '\t' || Peek() == '\r' || Peek() == '\n') Read(); }
    public void Expect(char c) { if (Read() != c) throw new InvalidDataException("Invalid calls.json; expected '" + c + "'."); }

    public string String(bool materialize, int capacity, out int decodedLength)
    {
        Expect('"');
        StringBuilder result = materialize ? new StringBuilder(capacity) : null;
        decodedLength = 0;
        while (true)
        {
            int value = Read();
            if (value < 0) throw new InvalidDataException("Unexpected end of JSON string.");
            if (value == '"') return materialize ? result.ToString() : null;
            if (value < 0x20) throw new InvalidDataException("Unescaped control character in JSON string.");
            if (value != '\\')
            {
                if (materialize) result.Append((char)value);
                decodedLength++;
                continue;
            }
            int escape = Read();
            char decoded;
            switch (escape)
            {
                case '"': decoded = '"'; break;
                case '\\': decoded = '\\'; break;
                case '/': decoded = '/'; break;
                case 'b': decoded = '\b'; break;
                case 'f': decoded = '\f'; break;
                case 'n': decoded = '\n'; break;
                case 'r': decoded = '\r'; break;
                case 't': decoded = '\t'; break;
                case 'u':
                    int code = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        int hex = Read();
                        int digit = hex >= '0' && hex <= '9' ? hex - '0' :
                            hex >= 'a' && hex <= 'f' ? hex - 'a' + 10 :
                            hex >= 'A' && hex <= 'F' ? hex - 'A' + 10 : -1;
                        if (digit < 0) throw new InvalidDataException("Invalid Unicode escape in JSON string.");
                        code = (code << 4) | digit;
                    }
                    decoded = (char)code;
                    break;
                default: throw new InvalidDataException("Invalid JSON string escape.");
            }
            if (materialize) result.Append(decoded);
            decodedLength++;
        }
    }

    public string NullableString(bool materialize, int capacity, out int decodedLength)
    {
        decodedLength = 0;
        if (Peek() == '"') return String(materialize, capacity, out decodedLength);
        if (Literal("null")) return null;
        throw new InvalidDataException("Expected a JSON string or null.");
    }

    public int Integer()
    {
        StringBuilder number = new StringBuilder();
        if (Peek() == '-') number.Append((char)Read());
        while (Peek() >= '0' && Peek() <= '9') number.Append((char)Read());
        int value;
        if (!Int32.TryParse(number.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            throw new InvalidDataException("Invalid call ID.");
        return value;
    }

    public void SkipValue()
    {
        White();
        if (Peek() == '"') { int ignored; String(false, 0, out ignored); return; }
        if (Peek() == '{')
        {
            Read(); White();
            if (Peek() == '}') { Read(); return; }
            while (true)
            {
                int ignored; String(false, 0, out ignored); White(); Expect(':'); SkipValue(); White();
                int separator = Read(); if (separator == '}') return; if (separator != ',') break; White();
            }
        }
        else if (Peek() == '[')
        {
            Read(); White();
            if (Peek() == ']') { Read(); return; }
            while (true)
            {
                SkipValue(); White(); int separator = Read();
                if (separator == ']') return; if (separator != ',') break; White();
            }
        }
        else
        {
            while (Peek() >= 0 && Peek() != ',' && Peek() != '}' && Peek() != ']' &&
                Peek() != ' ' && Peek() != '\t' && Peek() != '\r' && Peek() != '\n') Read();
        }
    }

    private bool Literal(string value)
    {
        if (Peek() != value[0]) return false;
        int saved = offset;
        for (int i = 0; i < value.Length; i++)
        {
            if (Read() != value[i]) { offset = saved; return false; }
        }
        return true;
    }
}

// History is read in two passes: first validate/count note characters, then
// allocate the note strings at their exact sizes. This avoids large temporary
// buffers when a history contains unusually long notes.
internal static class CallHistory
{
    public static int[] ValidateReadOnly(string path)
    {
        return ReadPass(path, true).ToArray();
    }

    public static List<CallRecord> Read(string path)
    {
        List<int> noteLengths = ReadPass(path, true);
        List<CallRecord> calls = new List<CallRecord>();
        using (StreamReader stream = new StreamReader(path, new UTF8Encoding(false, true), true, 65536))
        {
            JsonReader reader = new JsonReader(stream);
            reader.White(); reader.Expect('['); reader.White();
            if (reader.Peek() == ']')
            {
                reader.Read(); reader.White();
                if (reader.Peek() >= 0) throw new InvalidDataException("Unexpected data after calls.json.");
                return calls;
            }
            int index = 0;
            while (reader.Peek() >= 0)
            {
                calls.Add(ReadCall(reader, false, noteLengths[index++]).Record);
                reader.White();
                int separator = reader.Read();
                if (separator == ']') break;
                if (separator != ',') throw new InvalidDataException("Expected ',' or ']' in calls.json.");
                reader.White();
            }
            reader.White();
            if (reader.Peek() >= 0) throw new InvalidDataException("Unexpected data after calls.json.");
        }
        return calls;
    }

    private static List<int> ReadPass(string path, bool measureOnly)
    {
        List<int> lengths = new List<int>();
        using (StreamReader stream = new StreamReader(path, new UTF8Encoding(false, true), true, 65536))
        {
            JsonReader reader = new JsonReader(stream);
            reader.White(); reader.Expect('['); reader.White();
            if (reader.Peek() == ']')
            {
                reader.Read(); reader.White();
                if (reader.Peek() >= 0) throw new InvalidDataException("Unexpected data after calls.json.");
                return lengths;
            }
            while (reader.Peek() >= 0)
            {
                lengths.Add(ReadCall(reader, measureOnly, 0).NoteLength);
                reader.White();
                int separator = reader.Read();
                if (separator == ']') break;
                if (separator != ',') throw new InvalidDataException("Expected ',' or ']' in calls.json.");
                reader.White();
            }
            reader.White();
            if (reader.Peek() >= 0) throw new InvalidDataException("Unexpected data after calls.json.");
        }
        return lengths;
    }

    private sealed class ParsedCall
    {
        public CallRecord Record = new CallRecord();
        public int NoteLength;
    }

    private static ParsedCall ReadCall(JsonReader reader, bool measureOnly, int noteCapacity)
    {
        ParsedCall parsed = new ParsedCall();
        CallRecord call = parsed.Record;
        reader.Expect('{'); reader.White();
        if (reader.Peek() == '}') { reader.Read(); return parsed; }
        while (reader.Peek() >= 0)
        {
            int keyLength;
            string key = reader.String(true, 16, out keyLength);
            reader.White(); reader.Expect(':'); reader.White();
            int valueLength = 0;
            bool keep = !measureOnly;
            if (key == "Id") call.Id = reader.Integer();
            else if (key == "StartTime") call.StartTime = reader.NullableString(keep, 32, out valueLength);
            else if (key == "EndTime") call.EndTime = reader.NullableString(keep, 32, out valueLength);
            else if (key == "CallerName") call.CallerName = reader.NullableString(keep, 128, out valueLength);
            else if (key == "Location" || key == "StoreName")
                call.Location = reader.NullableString(keep, 128, out valueLength);
            else if (key == "Number") call.Number = reader.NullableString(keep, 64, out valueLength);
            else if (key == "Inc") call.Inc = reader.NullableString(keep, 64, out valueLength);
            else if (key == "Type") call.Type = reader.NullableString(keep, 64, out valueLength);
            else if (key == "Notes")
            {
                call.Notes = reader.NullableString(keep, noteCapacity, out valueLength);
                parsed.NoteLength = valueLength;
            }
            else if (key == "Status") call.Status = reader.NullableString(keep, 32, out valueLength);
            else reader.SkipValue();
            reader.White();
            int separator = reader.Read();
            if (separator == '}') break;
            if (separator != ',') throw new InvalidDataException("Expected ',' or '}' in a call record.");
            reader.White();
        }
        return parsed;
    }
}

// Immutable data captured on the UI thread for a background save.
internal sealed class CallSnapshot
{
    public int Id;
    public string StartTime, EndTime, CallerName, Location, Number, Inc, Type, Status, Notes;
    public NoteBuffer.Snapshot Note;
}

// Writes call snapshots as JSON without materializing another copy of each note.
internal static class CallWriter
{
    public static void Write(string path, CallSnapshot[] calls)
    {
        using (FileStream file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
        using (StreamWriter writer = new StreamWriter(file, new UTF8Encoding(false), 65536))
        {
            writer.Write('[');
            for (int i = 0; i < calls.Length; i++)
            {
                if (i > 0) writer.Write(',');
                CallSnapshot c = calls[i];
                writer.Write("{\"Id\":");
                writer.Write(c.Id.ToString(CultureInfo.InvariantCulture));
                writer.Write(",\"StartTime\":"); JsonOutput.WriteString(writer, c.StartTime);
                writer.Write(",\"EndTime\":"); JsonOutput.WriteString(writer, c.EndTime);
                writer.Write(",\"CallerName\":"); JsonOutput.WriteString(writer, c.CallerName);
                writer.Write(",\"Location\":"); JsonOutput.WriteString(writer, c.Location);
                writer.Write(",\"Number\":"); JsonOutput.WriteString(writer, c.Number);
                writer.Write(",\"Inc\":"); JsonOutput.WriteString(writer, c.Inc);
                writer.Write(",\"Type\":"); JsonOutput.WriteString(writer, c.Type);
                writer.Write(",\"Notes\":");
                if (c.Note != null) c.Note.WriteJson(writer); else JsonOutput.WriteString(writer, c.Notes);
                writer.Write(",\"Status\":"); JsonOutput.WriteString(writer, c.Status);
                writer.Write('}');
            }
            writer.Write(']');
        }
    }
}

internal static class CallNotesApp
{
    // Call types are also the settings rows and Ctrl+1..Ctrl+3 creation order.
    private static readonly string[] Types = { "Support", "Internal", "Other" };
    // Buffers are keyed by call ID so reordering or deleting list entries does
    // not accidentally attach an edited note to a different call.
    private static readonly Dictionary<int, NoteBuffer> NoteBuffers = new Dictionary<int, NoteBuffer>();
    private static List<CallRecord> calls;
    private static string dataDirectory;
    private static string dataFile;

    // Current call and editor state.
    private static int selected = -1;
    private static int nextCallId = 1;
    private static int caret;
    private static string focus = "Notes";
    private static int fieldCaret;
    private static int version;
    private static int savedVersion;
    private static DateTime saveAt;
    private static Task saveTask;
    private static Exception saveError;
    private static string status = "Ctrl+N New  |  F10 Settings  |  F3/F4 Calls  |  Ctrl+Q Quit";

    // Keep undo/redo histories per call so switching calls never mixes edits.
    private static readonly Dictionary<int, List<EditAction>> Undo = new Dictionary<int, List<EditAction>>();
    private static readonly Dictionary<int, List<EditAction>> Redo = new Dictionary<int, List<EditAction>>();
    // The renderer compares each frame with the last one and writes changed
    // rows only, avoiding flicker and needless terminal I/O while typing.
    private static string[] previousFrame;
    private static int[] previousRowStyles;
    private static int frameWidth;
    private static int frameHeight;
    private static int historyTop;
    private static readonly HashSet<int> selectedCardRows = new HashSet<int>();
    private static readonly HashSet<int> selectedCardBorderRows = new HashSet<int>();
    private static readonly HashSet<int> finishedNoteRows = new HashSet<int>();
    private static readonly Dictionary<int, string> noteTextByRow = new Dictionary<int, string>();
    private static readonly Dictionary<int, CallRecord> cardByRow = new Dictionary<int, CallRecord>();
    private static readonly Dictionary<int, bool> cardHeaderRows = new Dictionary<int, bool>();
    private static readonly Regex detailLabelPattern = new Regex(
        @"Caller:|Number:|Location:|INC:|Started:|Ended:|Duration:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex headingTokenPattern = new Regex(
        @"\b(?:Support|Internal|Other)\b|\[(?:Finished|Open)\]|(?:\d{4}-\d{2}-\d{2}T)?\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[+-]\d{2}:\d{2})?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex noteHighlightPattern = new Regex(
        @"(?<orange>#raise\b)|" +
        @"(?<blue>\b(?:msr|ped|datto|dpos)\b)|" +
        @"(?<pink>\b(?:kds|piks|pos|sn|tn|inc|dns|tr)\b|\b(?:inc)?\d+(?::\d{2})?(?:/\d{2,4})?\b|\[[^\]\r\n]*\])|" +
        @"(?<green>\([^()\r\n]*\))|(?<cyan>[\\/:.,<>])|(?<muted>={2,}|-{2,}|^_+$)|" +
        @"(?<marker>[\[\]{}*#])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static string theme = "Reference";
    private static readonly string[] themes = {
        "Reference", "Campbell", "Nord", "One Half Dark",
        "Solarized Dark", "Tango Dark", "Vintage", "Monochrome"
    };
    private static readonly ConsoleColor[] editableCallTypeColors = {
        ConsoleColor.DarkGray, ConsoleColor.DarkBlue, ConsoleColor.DarkGreen,
        ConsoleColor.DarkCyan, ConsoleColor.DarkRed, ConsoleColor.DarkMagenta,
        ConsoleColor.DarkYellow, ConsoleColor.Gray, ConsoleColor.Blue,
        ConsoleColor.Green, ConsoleColor.Cyan, ConsoleColor.Red,
        ConsoleColor.Magenta, ConsoleColor.Yellow, ConsoleColor.White
    };
    private static ConsoleColor callBoxOutlineColor = ConsoleColor.DarkGray;
    private static readonly string[] textColorKeys = {
        "TextColor.Heading", "TextColor.Body", "TextColor.Label",
        "TextColor.Status", "TextColor.Help", "TextColor.Muted",
        "TextColor.NoteHighlight"
    };
    private static readonly string[] textColorLabels = {
        "Heading text", "Main text", "Field labels", "Status text",
        "Help and footer", "Secondary text", "Note highlights"
    };
    private static readonly Dictionary<string, ConsoleColor> textColors =
        new Dictionary<string, ConsoleColor>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ConsoleColor> callTypeColors =
        new Dictionary<string, ConsoleColor>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, ConsoleColor> settingsColorRows =
        new Dictionary<int, ConsoleColor>();
    private static bool settingsOpen;
    private static bool searchOpen;
    private static bool searchComplete;
    private static string searchQuery = "";
    private static readonly List<int> searchMatches = new List<int>();
    private static int searchSelection;
    private static int searchCursorX;
    private static int searchCursorY;
    private static bool deleteConfirmation;
    private static int settingsSelection;
    private static int cursorX;
    private static int cursorY;
    private static string settingsFile;

    private sealed class EditAction
    {
        public string Field;
        public int Position;
        public string Removed;
        public string Inserted;
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && String.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("CallNotes C# 2.0");
                return 0;
            }
            if (args.Length == 2 && String.Equals(args[0], "--validate-json", StringComparison.OrdinalIgnoreCase))
            {
                int[] lengths = CallHistory.ValidateReadOnly(args[1]);
                int maximum = 0;
                foreach (int length in lengths) maximum = Math.Max(maximum, length);
                Console.WriteLine("Valid calls.json: {0} calls; maximum note {1:N0} characters; streaming read-only validation.",
                    lengths.Length, maximum);
                return 0;
            }
            if (args.Length == 1 && String.Equals(args[0], "--self-test", StringComparison.OrdinalIgnoreCase))
                return RunRoundTripTest();
            string defaultDataDirectory = GetDefaultDataDirectory();
            settingsFile = Path.Combine(defaultDataDirectory, "settings.json");
            dataDirectory = GetDataDirectory(args, defaultDataDirectory);
            dataFile = Path.Combine(dataDirectory, "calls.json");
            LoadTheme();
            calls = File.Exists(dataFile) ? NormalizeCalls(CallHistory.Read(dataFile)) : new List<CallRecord>();
            nextCallId = 1;
            foreach (CallRecord call in calls) nextCallId = Math.Max(nextCallId, call.Id + 1);
            if (calls.Count > 0)
            {
                selected = calls.Count - 1;
                caret = GetNotes(selected);
            }
            Run();
            return 0;
        }
        catch (Exception exception)
        {
            try { Console.ResetColor(); } catch { }
            try { Console.CursorVisible = true; } catch { }
            Console.Error.WriteLine("CallNotes error: " + exception.Message);
            return 1;
        }
    }

    private static string GetDefaultDataDirectory()
    {
        return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"));
    }

    private static string GetDataDirectory(string[] args, string defaultDirectory)
    {
        string directory = defaultDirectory;
        string settingsPath = Path.Combine(defaultDirectory, "settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                string settings = File.ReadAllText(settingsPath, Encoding.UTF8);
                string configured = ReadSettingsString(settings, "DataDirectory");
                if (!String.IsNullOrWhiteSpace(configured)) directory = Environment.ExpandEnvironmentVariables(configured);
            }
            catch { }
        }
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length) return Path.GetFullPath(args[i + 1]);
        }
        return Path.GetFullPath(directory);
    }

    private static string ReadSettingsString(string json, string property)
    {
        // Settings are small; this focused string reader lets updates preserve
        // unknown properties without adding a JSON package dependency.
        string marker = "\"" + property + "\"";
        int p = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (p < 0) return null;
        p = json.IndexOf(':', p + marker.Length);
        if (p < 0) return null;
        p++;
        while (p < json.Length && Char.IsWhiteSpace(json[p])) p++;
        if (p >= json.Length || json[p] != '"') return null;
        StringBuilder result = new StringBuilder();
        p++;
        while (p < json.Length && json[p] != '"')
        {
            char c = json[p++];
            if (c == '\\' && p < json.Length)
            {
                char e = json[p++];
                if (e == 'n') result.Append('\n');
                else if (e == 'r') result.Append('\r');
                else if (e == 't') result.Append('\t');
                else if (e == 'u' && p + 4 <= json.Length)
                {
                    int code;
                    if (Int32.TryParse(json.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                    {
                        result.Append((char)code);
                        p += 4;
                    }
                    else result.Append(e);
                }
                else result.Append(e);
            }

            else result.Append(c);
        }
        return result.ToString();
    }

    private static List<CallRecord> NormalizeCalls(List<CallRecord> loaded)
    {
        List<CallRecord> result = new List<CallRecord>();
        foreach (CallRecord c in loaded)
        {
            if (c == null || c.Id <= 0 || String.IsNullOrWhiteSpace(c.StartTime)) continue;
            if (c.EndTime == null) c.EndTime = "";
            if (c.CallerName == null) c.CallerName = "";
            if (c.Location == null) c.Location = "";
            if (c.Number == null) c.Number = "";
            if (c.Inc == null) c.Inc = "";
            if (String.IsNullOrWhiteSpace(c.Type)) c.Type = "Other";
            if (c.Notes == null) c.Notes = "";
            if (String.IsNullOrWhiteSpace(c.Status)) c.Status = "Open";
            result.Add(c);
        }
        return result;
    }

    // Startup and keyboard event loop.
    private static void Run()
    {
        bool previousControlCMode = Console.TreatControlCAsInput;
        try
        {
            // Ctrl+C must reach ReadKey as an application shortcut instead of
            // being treated as a console interrupt.
            Console.TreatControlCAsInput = true;
            Console.CursorVisible = false;
            bool running = true;
            while (running)
            {
            // Autosave work runs between input/render ticks; typing never waits
            // for disk I/O unless the user explicitly saves or quits.
            CompleteSaveIfReady();
            if (version != savedVersion && DateTime.UtcNow >= saveAt && (saveTask == null || saveTask.IsCompleted))
                StartSave();
            Draw();
            if (!Console.KeyAvailable) { Thread.Sleep(30); continue; }
            ConsoleKeyInfo key = Console.ReadKey(true);
            bool ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
            bool shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;
            if (searchOpen)
            {
                HandleSearchKey(key, ctrl);
                continue;
            }
            if (settingsOpen)
            {
                HandleSettingsKey(key);
                continue;
            }
            if (deleteConfirmation)
            {
                HandleDeleteConfirmationKey(key.Key);
                continue;
            }
            if (ctrl && key.Key == ConsoleKey.N) { NewCall(); continue; }
            if (ctrl && key.Key == ConsoleKey.F) { OpenSearch(); continue; }
            if (ctrl && key.Key >= ConsoleKey.D1 && (int)key.Key < (int)ConsoleKey.D1 + Types.Length)
            { NewCall((int)key.Key - (int)ConsoleKey.D1); continue; }
            if (ctrl && key.Key >= ConsoleKey.NumPad1 && (int)key.Key < (int)ConsoleKey.NumPad1 + Types.Length)
            { NewCall((int)key.Key - (int)ConsoleKey.NumPad1); continue; }
            if (ctrl && key.Key == ConsoleKey.S) { saveAt = DateTime.MinValue; StartSave(); status = "Saving calls..."; continue; }
            if (ctrl && key.Key == ConsoleKey.Q) { SaveAndWait(); running = false; continue; }
            if (ctrl && key.Key == ConsoleKey.C) { CopyCallToClipboard(); continue; }
            if (ctrl && key.Key == ConsoleKey.E && shift) { ExportCall(); continue; }
            if (ctrl && key.Key == ConsoleKey.E) { CopyNotesToClipboard(); continue; }
            if (ctrl && key.Key == ConsoleKey.D) { RequestDeleteCall(); continue; }
            if (ctrl && key.Key == ConsoleKey.Z && shift) { RedoEdit(); continue; }
            if (ctrl && key.Key == ConsoleKey.Z) { UndoEdit(); continue; }
            if (key.Key == ConsoleKey.F10) { settingsOpen = true; settingsSelection = 0; continue; }
            if (key.Key == ConsoleKey.F6) { SetExplicitTypeFocus(); continue; }
            if (key.Key == ConsoleKey.F2) { FinishCall(); continue; }
            if (key.Key == ConsoleKey.F3) { SelectCall(-1); continue; }
            if (key.Key == ConsoleKey.F4) { SelectCall(1); continue; }
            if (key.Key == ConsoleKey.PageUp) { ScrollHistory(-Math.Max(1, (Console.WindowHeight - 10) / 3)); continue; }
            if (key.Key == ConsoleKey.PageDown) { ScrollHistory(Math.Max(1, (Console.WindowHeight - 10) / 3)); continue; }
            if (key.Key == ConsoleKey.Tab) { CycleField(); continue; }
            if (key.Key == ConsoleKey.Escape) { focus = "Notes"; continue; }
            if (selected < 0) continue;
            if (focus == "Type")
            {
                if (key.Key == ConsoleKey.LeftArrow || key.Key == ConsoleKey.RightArrow) ChangeType(key.Key == ConsoleKey.LeftArrow ? -1 : 1);
                continue;
            }
            if (focus != "Notes")
            {
                if (ctrl && key.Key == ConsoleKey.LeftArrow) { fieldCaret = WordBoundary(GetField(selected, focus), fieldCaret, true); continue; }
                if (ctrl && key.Key == ConsoleKey.RightArrow) { fieldCaret = WordBoundary(GetField(selected, focus), fieldCaret, false); continue; }
                if (key.Key == ConsoleKey.LeftArrow) { fieldCaret = Math.Max(0, fieldCaret - 1); continue; }
                if (key.Key == ConsoleKey.RightArrow) { fieldCaret = Math.Min(GetField(selected, focus).Length, fieldCaret + 1); continue; }
                if (key.Key == ConsoleKey.Home || (ctrl && key.Key == ConsoleKey.Home)) { fieldCaret = 0; continue; }
                if (key.Key == ConsoleKey.End || (ctrl && key.Key == ConsoleKey.End)) { fieldCaret = GetField(selected, focus).Length; continue; }
                if (key.Key == ConsoleKey.Backspace && ctrl) { DeleteFieldWord(true); continue; }
                if (key.Key == ConsoleKey.Delete && ctrl) { DeleteFieldWord(false); continue; }
                if (key.Key == ConsoleKey.Backspace) { DeleteField(true); continue; }
                if (key.Key == ConsoleKey.Delete) { DeleteField(false); continue; }
                if (!ctrl && !Char.IsControl(key.KeyChar)) { EditField(key.KeyChar.ToString()); continue; }
                continue;
            }
            if (ctrl && key.Key == ConsoleKey.LeftArrow) { caret = GetNoteBuffer(selected).WordBoundary(caret, true); continue; }
            if (ctrl && key.Key == ConsoleKey.RightArrow) { caret = GetNoteBuffer(selected).WordBoundary(caret, false); continue; }
            if (key.Key == ConsoleKey.LeftArrow) { caret = Math.Max(0, caret - 1); continue; }
            if (key.Key == ConsoleKey.RightArrow) { caret = Math.Min(GetNotes(selected), caret + 1); continue; }
            if (key.Key == ConsoleKey.UpArrow) { MoveVertical(-1); continue; }
            if (key.Key == ConsoleKey.DownArrow) { MoveVertical(1); continue; }
            if (ctrl && key.Key == ConsoleKey.Home) { caret = 0; continue; }
            if (ctrl && key.Key == ConsoleKey.End) { caret = GetNotes(selected); continue; }
            if (key.Key == ConsoleKey.Home) { caret = LineStart(caret); continue; }
            if (key.Key == ConsoleKey.End) { caret = LineEnd(caret); continue; }
            if (key.Key == ConsoleKey.Backspace && ctrl) { DeleteNoteWord(true); continue; }
            if (key.Key == ConsoleKey.Delete && ctrl) { DeleteNoteWord(false); continue; }
            if (key.Key == ConsoleKey.Backspace) { Backspace(); continue; }
            if (key.Key == ConsoleKey.Delete) { Delete(); continue; }
            if (key.Key == ConsoleKey.Enter && focus == "Notes") { Insert("\n"); continue; }
            if (!ctrl && !Char.IsControl(key.KeyChar))
            {
                if (focus == "Notes") Insert(key.KeyChar.ToString());
                else EditField(key.KeyChar.ToString());
            }
            }
        }
        finally
        {
            Console.ResetColor();
            Console.CursorVisible = true;
            Console.TreatControlCAsInput = previousControlCMode;
        }
    }

    private static void Draw()
    {
        int width, height;
        try { width = Math.Max(1, Console.WindowWidth - 1); height = Math.Max(8, Console.WindowHeight); }
        catch { return; }
        string[] rows = new string[height];
        for (int i = 0; i < rows.Length; i++) rows[i] = "";
        rows[0] = " CALLNOTES / " + Path.GetFileName(dataDirectory) + " [" +
            CountCallsForDate(calls, DateTime.Today) + " calls today]  " + theme;
        selectedCardRows.Clear();
        selectedCardBorderRows.Clear();
        finishedNoteRows.Clear();
        noteTextByRow.Clear();
        cardByRow.Clear();
        cardHeaderRows.Clear();
        settingsColorRows.Clear();
        if (searchOpen) BuildSearchRows(rows, width, height);
        else if (settingsOpen) BuildSettingsRows(rows, width, height);
        else BuildCallRows(rows, width, height);
        // Preserve unchanged rows and styles so the terminal only redraws
        // content that has actually changed.
        for (int y = 0; y < height; y++)
        {
            string visible = Clip(rows[y], width);
            string text = visible.PadRight(width);
            int rowStyle = SetRowColors(rows[y], y);
            bool changed = previousFrame == null || y >= previousFrame.Length ||
                !String.Equals(visible, previousFrame[y].TrimEnd(), StringComparison.Ordinal) ||
                previousRowStyles == null || y >= previousRowStyles.Length || rowStyle != previousRowStyles[y];
            if (changed)
            {
                try
                {
                    Console.SetCursorPosition(0, y);
                    string noteText;
                    if (noteTextByRow.TryGetValue(y, out noteText))
                        WriteNoteRow(noteText, width, rowStyle, finishedNoteRows.Contains(y),
                            selectedCardRows.Contains(y));
                    else if (searchOpen)
                        Console.Write(visible.PadRight(width));
                    else if (settingsColorRows.ContainsKey(y))
                        WriteSettingsColorRow(visible, width, settingsColorRows[y]);
                    else if (cardByRow.ContainsKey(y))
                        WriteCallCardRow(rows[y], width, cardByRow[y], cardHeaderRows[y]);
                    else
                        Console.Write(text);
                }
                catch { previousFrame = null; return; }
            }
        }
        Console.ResetColor();
        frameWidth = width;
        frameHeight = height;
        previousFrame = new string[height];
        previousRowStyles = new int[height];
        for (int i = 0; i < height; i++) previousFrame[i] = Clip(rows[i], width).PadRight(width);
        for (int i = 0; i < height; i++) previousRowStyles[i] = GetRowStyle(rows[i], i);
        try
        {
            int drawCursorX = searchOpen ? searchCursorX : cursorX;
            int drawCursorY = searchOpen ? searchCursorY : cursorY;
            Console.SetCursorPosition(Math.Max(0, Math.Min(width - 1, drawCursorX)), Math.Max(0, Math.Min(height - 1, drawCursorY)));
            Console.CursorVisible = searchOpen ? !searchComplete : !settingsOpen;
        }
        catch { }
    }

    // Terminal layout and card rendering.
    private static void BuildCallRows(string[] rows, int width, int height)
    {
        int footer = Math.Max(2, height - 3);
        cursorX = 1;
        cursorY = footer - 1;
        rows[footer] = " Ctrl+1..3 New | Ctrl+N New | Ctrl+F Search | F3/F4 Select | PgUp/PgDn History | F6 Type | Tab Fields | F10 Settings | Ctrl+E Copy Notes | Ctrl+D Delete";
        rows[footer + 1] = " Ctrl+Z Undo / Ctrl+Shift+Z Redo | Ctrl+Left/Right Word | Ctrl+Backspace/Delete Word | F2 Finish | Ctrl+S Save | Ctrl+Q Quit";
        rows[height - 1] = " " + status;
        if (selected < 0 || calls.Count == 0)
        {
            rows[1] = " No calls yet. Press Ctrl+N or Ctrl+1..Ctrl+3 to start.";
            return;
        }
        if (height < 12)
        {
            CallRecord compact = calls[selected];
            string summary = "> Call #" + GetDailyCallNumber(selected) + " / " + compact.Type +
                " [" + compact.Status + "] Number: " + compact.Number;
            rows[2] = BoxContent(summary, width);
            selectedCardRows.Add(2);
            if (width >= 4)
            {
                rows[1] = BoxBorder(width);
                rows[3] = BoxBorder(width);
                selectedCardBorderRows.Add(1);
                selectedCardBorderRows.Add(3);
            }
            cursorY = 2;
            cursorX = Math.Min(width - 1, focus == "Notes" ? 3 : 3 + fieldCaret);
            return;
        }
        int contentEnd = footer - 1;
        int availableRows = Math.Max(1, contentEnd - 2);
        // Reserve enough rows for the selected note to grow, then use remaining
        // space for neighboring calls in the history.
        int selectedNoteRows = Math.Min(Math.Max(1, availableRows - 6),
            GetNoteVisualLineCount(GetNoteBuffer(selected), width, Math.Max(1, availableRows - 6)));
        int selectedCardHeight = selectedNoteRows + 6;
        int visibleCards = Math.Max(1, 1 + Math.Max(0, availableRows - selectedCardHeight) / 8);
        historyTop = Math.Max(0, Math.Min(historyTop, Math.Max(0, calls.Count - visibleCards)));
        if (selected < historyTop) historyTop = selected;
        if (selected >= historyTop + visibleCards) historyTop = selected - visibleCards + 1;
        rows[1] = " HISTORY  (showing " + (historyTop + 1) + "-" + Math.Min(calls.Count, historyTop + visibleCards) +
            ")   Selected #" + GetDailyCallNumber(selected);
        int y = 2;
        int selectedFieldX = 2;
        int selectedFieldY = y + 1;
        for (int i = historyTop; i < calls.Count && i < historyTop + visibleCards && y < contentEnd; i++)
        {
            CallRecord c = calls[i];
            bool isSelected = i == selected;
            int noteRows = isSelected ? selectedNoteRows : 1;
            int cardHeight = noteRows + 6;
            if (y + cardHeight > contentEnd) break;
            string marker = i == selected ? ">" : " ";
            string heading = marker + " Call #" + GetDailyCallNumber(i) + " / ";
            rows[y] = BoxBorder(width);
            if (isSelected) selectedCardBorderRows.Add(y);
            rows[y + 1] = BoxContent(heading + c.Type + "  [" + c.Status + "]", width);
            cardByRow[y + 1] = c;
            cardHeaderRows[y + 1] = true;
            string detail = "Caller: ";
            int callerX = 1 + detail.Length;
            detail += c.CallerName + "  ";
            detail += "Number: ";
            int numberX = 1 + detail.Length;
            detail += c.Number;
            detail += "  Location: ";
            int locationX = 1 + detail.Length;
            detail += c.Location;
            int incX = 1 + detail.Length;
            detail += "  INC: "; incX = 1 + detail.Length; detail += c.Inc;
            rows[y + 2] = BoxContent(detail, width);
            cardByRow[y + 2] = c;
            cardHeaderRows[y + 2] = false;
            rows[y + 3] = BoxContent(BuildCallTiming(c), width);
            cardByRow[y + 3] = c;
            cardHeaderRows[y + 3] = false;
            if (isSelected)
            {
                selectedCardRows.Add(y + 1);
                selectedCardRows.Add(y + 2);
                selectedCardRows.Add(y + 3);
            }
            rows[y + 4] = BoxContent("", width);
            if (isSelected)
            {
                selectedFieldY = y + 2;
                if (focus == "Type") { selectedFieldY = y + 1; selectedFieldX = 1 + heading.Length; }
                else if (focus == "CallerName") selectedFieldX = callerX;
                else if (focus == "Location") selectedFieldX = locationX;
                else if (focus == "Inc") selectedFieldX = incX;
                else selectedFieldX = numberX;
            }
            NoteBuffer note = GetNoteBuffer(i);
            if (!isSelected)
            {
                int n = Math.Min(note.Length, Math.Max(0, width - 15));
                string preview = n == 0 ? "(no notes)" : note.GetRange(Math.Max(0, note.Length - n), n).Replace('\n', ' ');
                string notePreview = "Notes: " + preview;
                rows[y + 5] = BoxContent(notePreview, width);
                noteTextByRow[y + 5] = notePreview;
                if (c.Status == "Finished") finishedNoteRows.Add(y + 5);
                cardByRow[y + 5] = c;
                cardHeaderRows[y + 5] = false;
            }
            else
            {
                DrawInlineNoteRows(rows, y + 5, note, width, noteRows);
                if (c.Status == "Finished")
                    for (int noteRow = 0; noteRow < noteRows; noteRow++)
                        finishedNoteRows.Add(y + 5 + noteRow);
            }
            rows[y + cardHeight - 1] = BoxBorder(width);
            if (isSelected) selectedCardBorderRows.Add(y + cardHeight - 1);
            for (int row = y; row < y + cardHeight - 1; row++)
            {
                if (row != y) selectedCardRows.Add(row);
            }
            y += cardHeight + 1;
        }
        if (focus != "Notes")
        {
            cursorY = Math.Min(footer - 1, selectedFieldY);
            cursorX = Math.Min(width - 1, selectedFieldX + (focus == "Type" ? 0 : fieldCaret));
        }
    }

    private static string BuildCallTiming(CallRecord call)
    {
        StringBuilder timing = new StringBuilder();
        DateTime started;
        bool hasStarted = DateTime.TryParse(call.StartTime, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out started);
        if (hasStarted)
            timing.Append("Started: ").Append(FormatCallDateTime(started));

        DateTime ended;
        bool hasEnded = DateTime.TryParse(call.EndTime, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out ended);
        if (hasEnded)
        {
            if (timing.Length > 0) timing.Append("  |  ");
            timing.Append("Ended: ").Append(FormatCallDateTime(ended));
        }
        if (call.Status == "Finished" && hasStarted && hasEnded && ended >= started)
        {
            timing.Append("  |  Duration: ").Append(FormatCallDuration(ended - started));
        }
        return timing.ToString();
    }

    private static int CountCallsForDate(IList<CallRecord> history, DateTime date)
    {
        int count = 0;
        DateTime targetDate = date.Date;
        foreach (CallRecord call in history)
        {
            DateTime started;
            if (call != null &&
                DateTime.TryParse(call.StartTime, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out started) &&
                GetLocalCallDate(started) == targetDate)
                count++;
        }
        return count;
    }

    private static int GetDailyCallNumber(int callIndex)
    {
        if (callIndex < 0 || callIndex >= calls.Count) return 0;
        DateTime targetStart;
        if (!DateTime.TryParse(calls[callIndex].StartTime, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out targetStart)) return 0;

        DateTime targetDate = targetStart.ToLocalTime().Date;
        int number = 0;
        for (int i = 0; i <= callIndex; i++)
        {
            DateTime started;
            if (DateTime.TryParse(calls[i].StartTime, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out started) &&
                GetLocalCallDate(started) == targetDate)
                number++;
        }
        return number;
    }

    private static DateTime GetLocalCallDate(DateTime started)
    {
        return started.Kind == DateTimeKind.Utc ? started.ToLocalTime().Date : started.Date;
    }

    private static int GetDailyCallNumber(CallRecord call)
    {
        for (int i = 0; i < calls.Count; i++)
            if (calls[i].Id == call.Id)
                return GetDailyCallNumber(i);
        return 0;
    }

    private static string FormatCallDateTime(DateTime value)
    {
        return value.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    private static string GetFormattedCallDate(string value)
    {
        DateTime parsed;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out parsed) ? FormatCallDateTime(parsed) : "";
    }

    private static string FormatCallDuration(TimeSpan duration)
    {
        long totalSeconds = (long)duration.TotalSeconds;
        long days = totalSeconds / 86400;
        long hours = (totalSeconds % 86400) / 3600;
        long minutes = (totalSeconds % 3600) / 60;
        long seconds = totalSeconds % 60;
        if (days > 0)
            return days.ToString(CultureInfo.InvariantCulture) + "d " +
                hours.ToString("00", CultureInfo.InvariantCulture) + "h " +
                minutes.ToString("00", CultureInfo.InvariantCulture) + "m";
        if (hours > 0)
            return hours.ToString(CultureInfo.InvariantCulture) + "h " +
                minutes.ToString("00", CultureInfo.InvariantCulture) + "m " +
                seconds.ToString("00", CultureInfo.InvariantCulture) + "s";
        if (minutes > 0)
            return minutes.ToString(CultureInfo.InvariantCulture) + "m " +
                seconds.ToString("00", CultureInfo.InvariantCulture) + "s";
        return seconds.ToString(CultureInfo.InvariantCulture) + "s";
    }

    private static void DrawInlineNoteRows(string[] rows, int firstRow, NoteBuffer note, int width, int rowCount)
    {
        int innerWidth = Math.Max(1, width - 4);
        int capacity = Math.Max(1, innerWidth * rowCount);
        // Render only the visible slice of a long note and keep the caret in view.
        int start = GetNoteViewportStart(note, caret, innerWidth, rowCount);
        int count = Math.Min(capacity, note.Length - start);
        List<StringBuilder> lines = new List<StringBuilder>();
        for (int line = 0; line < rowCount; line++) lines.Add(new StringBuilder());
        int lineIndex = 0, column = 0;
        bool cursorPlaced = false;
        for (int offset = 0; offset < count && lineIndex < rowCount; offset++)
        {
            int absolute = start + offset;
            char current = note.GetCharAt(absolute);
            if (current == '\r') continue;
            if (current == '\n')
            {
                if (!cursorPlaced && caret == absolute)
                {
                    cursorY = firstRow + lineIndex;
                    cursorX = 2 + column;
                    cursorPlaced = true;
                }
                lineIndex++;
                column = 0;
                continue;
            }
            if (column >= innerWidth)
            {
                lineIndex++;
                column = 0;
                if (lineIndex >= rowCount) break;
            }
            if (!cursorPlaced && caret == absolute)
            {
                cursorY = firstRow + lineIndex;
                cursorX = 2 + column;
                cursorPlaced = true;
            }
            lines[lineIndex].Append(current);
            column++;
        }
        if (!cursorPlaced && caret == start + count)
        {
            if (column >= innerWidth && lineIndex + 1 < rowCount) { lineIndex++; column = 0; }
            if (lineIndex < rowCount)
            {
                cursorY = firstRow + lineIndex;
                cursorX = 2 + column;
            }
        }
        for (int line = 0; line < rowCount; line++)
        {
            string content = lines[line].Length == 0 && line == 0 ? "(no notes)" : lines[line].ToString();
            rows[firstRow + line] = BoxContent(" " + content, width);
            noteTextByRow[firstRow + line] = content;
        }
        if (start > 0)
        {
            rows[firstRow] = BoxContent(" ... earlier note text ...", width);
            noteTextByRow[firstRow] = "... earlier note text ...";
        }
        if (start + count < note.Length)
        {
            rows[firstRow + rowCount - 1] = BoxContent(" ... later note text ...", width);
            noteTextByRow[firstRow + rowCount - 1] = "... later note text ...";
        }
    }

    private static void WriteNoteRow(string noteText, int width, int rowStyle, bool finished, bool activeCall)
    {
        ConsoleColor baseForeground = (ConsoleColor)(rowStyle & 0x0F);
        ConsoleColor baseBackground = (ConsoleColor)((rowStyle >> 4) & 0x0F);
        // Completed calls are intentionally plain gray for easier history review.
        if (finished) baseForeground = GetThemeFinishedColor();
        Console.ForegroundColor = baseForeground;
        Console.BackgroundColor = baseBackground;
        if (width < 2)
        {
            Console.Write(Clip(noteText, width));
            return;
        }
        Console.ForegroundColor = activeCall ? GetBoldOutlineColor(callBoxOutlineColor) : GetThemeBorderColor();
        Console.Write('|');
        Console.ForegroundColor = baseForeground;
        Console.Write(' ');
        bool hasLabel = noteText.StartsWith("Notes: ", StringComparison.Ordinal);
        if (hasLabel)
        {
            Console.ForegroundColor = finished ? GetThemeFinishedColor() : GetThemeLabelColor();
            Console.Write("Notes:");
            Console.ForegroundColor = baseForeground;
            noteText = noteText.Substring(6);
        }
        int maximumText = Math.Max(0, width - 3 - (hasLabel ? 6 : 0));
        string visible = Clip(noteText, maximumText);
        int position = 0;
        if (!finished)
        {
            foreach (Match match in noteHighlightPattern.Matches(visible))
            {
                if (match.Index > position)
                {
                    Console.ForegroundColor = baseForeground;
                    Console.Write(visible.Substring(position, match.Index - position));
                }
                Console.ForegroundColor = GetNoteHighlightColor(match);
                Console.Write(match.Value);
                position = match.Index + match.Length;
            }
        }
        if (position < visible.Length)
        {
            Console.ForegroundColor = baseForeground;
            Console.Write(visible.Substring(position));
        }
        Console.ForegroundColor = baseForeground;
        Console.Write(new string(' ', Math.Max(0, width - 3 - (hasLabel ? 6 : 0) - visible.Length)));
        Console.ForegroundColor = activeCall ? GetBoldOutlineColor(callBoxOutlineColor) : GetThemeBorderColor();
        Console.Write('|');
    }

    private static void WriteCallCardRow(string row, int width, CallRecord call, bool header)
    {
        bool activeCall = selected >= 0 && selected < calls.Count && calls[selected].Id == call.Id;
        ConsoleColor borderColor = activeCall ? GetBoldOutlineColor(callBoxOutlineColor) : GetThemeBorderColor();
        Console.ForegroundColor = borderColor;
        Console.BackgroundColor = ConsoleColor.Black;
        if (width < 2)
        {
            Console.Write(Clip(row, width));
            return;
        }

        Console.Write('|');
        string content = row.Length > 1 ? row.Substring(1, Math.Max(0, Math.Min(width - 2, row.Length - 2))) : "";
        int contentLength = Math.Max(0, width - 2);
        if (header)
        {
            WriteHeaderSegments(content, call);
        }
        else
        {
            WriteDetailSegments(content, call);
        }
        Console.ForegroundColor = borderColor;
        Console.Write('|');
        Console.BackgroundColor = ConsoleColor.Black;
    }

    private static void WriteHeaderSegments(string content, CallRecord call)
    {
        MatchCollection matches = headingTokenPattern.Matches(content);
        int position = 0;
        foreach (Match match in matches)
        {
            if (match.Index < position) continue;
            Console.ForegroundColor = call.Status == "Finished" ? GetThemeFinishedColor() :
                GetConfiguredTextColor("TextColor.Heading", GetThemeForegroundColor());
            Console.Write(content.Substring(position, match.Index - position));
            Console.ForegroundColor = GetHeadingColor(match.Value, call);
            Console.Write(match.Value);
            position = match.Index + match.Length;
        }
        if (position < content.Length)
        {
            Console.ForegroundColor = call.Status == "Finished" ? GetThemeFinishedColor() :
                GetConfiguredTextColor("TextColor.Heading", GetThemeForegroundColor());
            Console.Write(content.Substring(position));
        }
    }

    private static ConsoleColor GetHeadingColor(string token, CallRecord call)
    {
        if (token.StartsWith("[", StringComparison.Ordinal))
            return call.Status == "Finished" ? GetThemeSuccessColor() : GetThemeWarningColor();
        if (token.IndexOf(':') >= 0 || Char.IsDigit(token[0]))
            return GetThemeMutedColor();
        if (String.Equals(token, call.Type, StringComparison.OrdinalIgnoreCase))
            return GetCallTypeColor(call.Type);
        return GetConfiguredTextColor("TextColor.Heading", GetThemeForegroundColor());
    }

    private static ConsoleColor GetCallTypeColor(string type)
    {
        if (theme == "Monochrome") return GetThemeForegroundColor();
        ConsoleColor configured;
        return callTypeColors.TryGetValue(type ?? "", out configured)
            ? configured : GetDefaultCallTypeColor(type);
    }

    private static ConsoleColor GetDefaultCallTypeColor(string type)
    {
        if (String.Equals(type, "Support", StringComparison.OrdinalIgnoreCase)) return ConsoleColor.DarkCyan;
        if (String.Equals(type, "Internal", StringComparison.OrdinalIgnoreCase)) return ConsoleColor.DarkYellow;
        if (String.Equals(type, "Other", StringComparison.OrdinalIgnoreCase)) return ConsoleColor.DarkMagenta;
        return ConsoleColor.Gray;
    }

    private static ConsoleColor GetBoldOutlineColor(ConsoleColor color)
    {
        if (color == ConsoleColor.DarkGray) return ConsoleColor.Gray;
        if (color >= ConsoleColor.Black && color <= ConsoleColor.DarkYellow)
            return (ConsoleColor)((int)color + 8);
        return color;
    }

    private static ConsoleColor GetConfiguredTextColor(string key, ConsoleColor fallback)
    {
        ConsoleColor configured;
        return textColors.TryGetValue(key, out configured) ? configured : fallback;
    }

    private static ConsoleColor GetThemeForegroundColor()
    {
        return GetConfiguredTextColor("TextColor.Body", ConsoleColor.Gray);
    }

    private static ConsoleColor GetThemeMutedColor()
    {
        return GetConfiguredTextColor("TextColor.Muted",
            theme == "Vintage" ? ConsoleColor.DarkGreen : ConsoleColor.DarkGray);
    }

    private static ConsoleColor GetThemeAccentColor()
    {
        ConsoleColor fallback;
        switch (theme)
        {
            case "Nord": fallback = ConsoleColor.Cyan; break;
            case "Campbell": fallback = ConsoleColor.Blue; break;
            case "One Half Dark": fallback = ConsoleColor.Magenta; break;
            case "Solarized Dark": fallback = ConsoleColor.Green; break;
            case "Tango Dark": fallback = ConsoleColor.Yellow; break;
            case "Vintage": fallback = ConsoleColor.DarkGreen; break;
            case "Monochrome": fallback = ConsoleColor.Gray; break;
            default: fallback = ConsoleColor.DarkCyan; break;
        }
        return GetConfiguredTextColor("TextColor.Heading", fallback);
    }

    private static ConsoleColor GetThemeBorderColor()
    {
        switch (theme)
        {
            case "Nord": return ConsoleColor.DarkCyan;
            case "Campbell": return ConsoleColor.DarkBlue;
            case "One Half Dark": return ConsoleColor.DarkBlue;
            case "Solarized Dark": return ConsoleColor.DarkBlue;
            case "Tango Dark": return ConsoleColor.DarkMagenta;
            case "Vintage": return ConsoleColor.DarkGreen;
            case "Monochrome": return ConsoleColor.DarkGray;
            default: return ConsoleColor.DarkGray;
        }
    }

    private static ConsoleColor GetThemeLabelColor()
    {
        ConsoleColor fallback;
        switch (theme)
        {
            case "Nord": fallback = ConsoleColor.Cyan; break;
            case "Campbell": fallback = ConsoleColor.DarkCyan; break;
            case "One Half Dark": fallback = ConsoleColor.Cyan; break;
            case "Solarized Dark": fallback = ConsoleColor.DarkGreen; break;
            case "Tango Dark": fallback = ConsoleColor.DarkBlue; break;
            case "Vintage": fallback = ConsoleColor.DarkYellow; break;
            case "Monochrome": fallback = ConsoleColor.Gray; break;
            default: fallback = ConsoleColor.DarkCyan; break;
        }
        return GetConfiguredTextColor("TextColor.Label", fallback);
    }

    private static ConsoleColor GetThemeFinishedColor()
    {
        return GetConfiguredTextColor("TextColor.Body",
            theme == "Vintage" ? ConsoleColor.DarkGreen : ConsoleColor.DarkGray);
    }

    private static ConsoleColor GetThemeSuccessColor()
    {
        return GetConfiguredTextColor("TextColor.Status",
            theme == "Tango Dark" ? ConsoleColor.Green :
            theme == "Monochrome" ? ConsoleColor.Gray : ConsoleColor.DarkGreen);
    }

    private static ConsoleColor GetThemeWarningColor()
    {
        return GetConfiguredTextColor("TextColor.Status",
            theme == "One Half Dark" ? ConsoleColor.Yellow :
            theme == "Monochrome" ? ConsoleColor.Gray : ConsoleColor.DarkYellow);
    }

    private static void WriteDetailSegments(string content, CallRecord call)
    {
        MatchCollection labels = detailLabelPattern.Matches(content);
        if (labels.Count == 0)
        {
            Console.ForegroundColor = call.Status == "Finished" ? GetThemeFinishedColor() : GetThemeForegroundColor();
            Console.Write(content);
            return;
        }

        int position = 0;
        foreach (Match label in labels)
        {
            if (label.Index < position) continue;
            Console.ForegroundColor = call.Status == "Finished" ? GetThemeFinishedColor() : GetThemeForegroundColor();
            Console.Write(content.Substring(position, label.Index - position));
            Console.ForegroundColor = GetDetailLabelColor(label.Value, call);
            Console.Write(label.Value);
            int valueStart = label.Index + label.Length;
            Match nextLabel = null;
            for (int i = labels.Count - 1; i >= 0; i--)
            {
                if (labels[i].Index > label.Index) nextLabel = labels[i];
            }
            int valueEnd = nextLabel == null ? content.Length : nextLabel.Index;
            Console.ForegroundColor = GetDetailValueColor(label.Value, call);
            Console.Write(content.Substring(valueStart, Math.Max(0, valueEnd - valueStart)));
            position = valueEnd;
            if (nextLabel == null) break;
        }
        if (position < content.Length)
        {
            Console.ForegroundColor = call.Status == "Finished" ? GetThemeFinishedColor() : GetThemeForegroundColor();
            Console.Write(content.Substring(position));
        }
    }

    private static ConsoleColor GetDetailLabelColor(string label, CallRecord call)
    {
        ConsoleColor fallback = label.StartsWith("INC", StringComparison.OrdinalIgnoreCase) &&
            theme == "Reference" ? ConsoleColor.DarkMagenta : GetThemeLabelColor();
        return GetConfiguredTextColor("TextColor.Label", fallback);
    }

    private static ConsoleColor GetDetailValueColor(string label, CallRecord call)
    {
        if (theme == "Monochrome") return call.Status == "Finished" ? GetThemeFinishedColor() : GetThemeForegroundColor();
        if (label.StartsWith("Started", StringComparison.OrdinalIgnoreCase) ||
            label.StartsWith("Ended", StringComparison.OrdinalIgnoreCase) ||
            label.StartsWith("Duration", StringComparison.OrdinalIgnoreCase))
            return GetThemeForegroundColor();
        if (label.StartsWith("Caller", StringComparison.OrdinalIgnoreCase)) return GetThemeForegroundColor();
        if (label.StartsWith("Number", StringComparison.OrdinalIgnoreCase))
            return GetConfiguredTextColor("TextColor.Body", ConsoleColor.DarkYellow);
        if (label.StartsWith("Location", StringComparison.OrdinalIgnoreCase))
            return GetConfiguredTextColor("TextColor.Body", ConsoleColor.DarkCyan);
        if (label.StartsWith("INC", StringComparison.OrdinalIgnoreCase))
            return GetConfiguredTextColor("TextColor.Body",
                theme == "Reference" ? ConsoleColor.DarkMagenta : GetThemeAccentColor());
        return GetThemeForegroundColor();
    }

    private static ConsoleColor GetNoteHighlightColor(Match match)
    {
        return GetConfiguredTextColor("TextColor.NoteHighlight", GetDefaultNoteHighlightColor(match));
    }

    private static ConsoleColor GetDefaultNoteHighlightColor(Match match)
    {
        if (theme == "Monochrome") return ConsoleColor.Gray;
        if (theme == "Nord" || theme == "One Half Dark")
        {
            if (match.Groups["orange"].Success) return theme == "Nord" ? ConsoleColor.DarkYellow : ConsoleColor.Red;
            if (match.Groups["yellow"].Success) return ConsoleColor.Yellow;
            if (match.Groups["blue"].Success) return ConsoleColor.Cyan;
            if (match.Groups["pink"].Success) return ConsoleColor.Magenta;
            if (match.Groups["green"].Success) return ConsoleColor.Green;
            if (match.Groups["cyan"].Success) return theme == "Nord" ? ConsoleColor.DarkCyan : ConsoleColor.Blue;
            return ConsoleColor.DarkGray;
        }
        if (theme == "Solarized Dark" || theme == "Tango Dark" || theme == "Vintage")
        {
            if (match.Groups["orange"].Success)
                return theme == "Tango Dark" ? ConsoleColor.Red : ConsoleColor.DarkYellow;
            if (match.Groups["yellow"].Success)
                return theme == "Vintage" ? ConsoleColor.DarkYellow : ConsoleColor.Yellow;
            if (match.Groups["blue"].Success)
                return theme == "Vintage" ? ConsoleColor.DarkCyan : ConsoleColor.Blue;
            if (match.Groups["pink"].Success)
                return theme == "Vintage" ? ConsoleColor.DarkGreen : ConsoleColor.Magenta;
            if (match.Groups["green"].Success)
                return theme == "Vintage" ? ConsoleColor.DarkGreen : ConsoleColor.Green;
            if (match.Groups["cyan"].Success)
                return theme == "Vintage" ? ConsoleColor.DarkGray : ConsoleColor.Cyan;
            return GetThemeMutedColor();
        }
        if (match.Groups["orange"].Success) return ConsoleColor.DarkYellow;
        if (match.Groups["yellow"].Success) return ConsoleColor.Yellow;
        if (match.Groups["blue"].Success) return ConsoleColor.Blue;
        if (match.Groups["pink"].Success) return ConsoleColor.Magenta;
        if (match.Groups["green"].Success) return ConsoleColor.Green;
        if (match.Groups["cyan"].Success) return ConsoleColor.DarkCyan;
        if (match.Groups["muted"].Success || match.Groups["marker"].Success) return ConsoleColor.DarkGray;
        return ConsoleColor.DarkMagenta;
    }

    private static int GetNoteViewportStart(NoteBuffer note, int caret, int lineWidth, int rowCount)
    {
        int lines = 1;
        int column = 0;
        for (int position = Math.Min(caret, note.Length) - 1; position >= 0; position--)
        {
            char value = note.GetCharAt(position);
            if (value == '\r') continue;
            if (value == '\n')
            {
                if (lines >= rowCount) return position + 1;
                lines++;
                column = 0;
                continue;
            }
            if (column >= lineWidth)
            {
                if (lines >= rowCount) return position + 1;
                lines++;
                column = 0;
            }
            column++;
        }
        return 0;
    }

    private static int GetNoteVisualLineCount(NoteBuffer note, int width, int maximumLines)
    {
        int lineWidth = Math.Max(1, width - 4);
        int lines = 1;
        int column = 0;
        int maximumCharacters = lineWidth * maximumLines;
        for (int position = 0; position < note.Length && position < maximumCharacters; position++)
        {
            char value = note.GetCharAt(position);
            if (value == '\r') continue;
            if (value == '\n')
            {
                if (++lines >= maximumLines) return maximumLines;
                column = 0;
                continue;
            }
            if (column >= lineWidth)
            {
                if (++lines >= maximumLines) return maximumLines;
                column = 0;
            }
            column++;
        }
        return lines;
    }

    private static NoteBuffer GetNoteBuffer(int index)
    {
        CallRecord call = calls[index];
        NoteBuffer buffer;
        if (!NoteBuffers.TryGetValue(call.Id, out buffer))
        {
            buffer = new NoteBuffer(call.Notes);
            NoteBuffers.Add(call.Id, buffer);
        }
        return buffer;
    }

    private static int GetNotes(int index)
    {
        return GetNoteBuffer(index).Length;
    }

    private static void NewCall()
    {
        NewCall(-1);
    }

    private static void NewCall(int requestedType)
    {
        string type = requestedType >= 0 && requestedType < Types.Length ? Types[requestedType] : ReadDefaultCallType();
        CallRecord call = new CallRecord();
        call.Id = nextCallId++;
        call.StartTime = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
        call.EndTime = call.CallerName = call.Location = call.Number = call.Inc = call.Notes = "";
        call.Type = type;
        call.Status = "Open";
        calls.Add(call);
        selected = calls.Count - 1;
        focus = "Number";
        fieldCaret = 0;
        caret = 0;
        Changed("Created call #" + GetDailyCallNumber(selected));
    }

    private static string ReadDefaultCallType()
    {
        if (File.Exists(settingsFile))
        {
            try
            {
                string type = ReadSettingsString(File.ReadAllText(settingsFile, Encoding.UTF8), "DefaultCallType");
                foreach (string candidate in Types) if (String.Equals(candidate, type, StringComparison.OrdinalIgnoreCase)) return candidate;
            }
            catch { }
        }
        return "Support";
    }

    // Call selection, structured fields, and note editing.
    private static void SelectCall(int delta)
    {
        if (calls.Count == 0) return;
        selected = Math.Max(0, Math.Min(calls.Count - 1, selected + delta));
        focus = "Notes";
        caret = GetNotes(selected);
        status = "Selected call #" + GetDailyCallNumber(selected);
    }

    private static void CycleField()
    {
        if (selected < 0) { status = "Create a call first"; return; }
        string[] fields = { "Number", "Location", "CallerName", "Inc", "Notes" };
        int index = Array.IndexOf(fields, focus);
        focus = fields[(index + 1 + fields.Length) % fields.Length];
        fieldCaret = GetField(selected, focus).Length;
    }

    private static void ChangeType(int delta)
    {
        int index = Array.IndexOf(Types, calls[selected].Type);
        calls[selected].Type = Types[(index + delta + Types.Length) % Types.Length];
        Changed("Call type: " + calls[selected].Type);
    }

    private static void EditField(string value)
    {
        string current = GetField(selected, focus);
        if (fieldCaret < 0 || fieldCaret > current.Length) fieldCaret = current.Length;
        RecordEdit(new EditAction { Field = focus, Position = fieldCaret, Removed = "", Inserted = value });
        SetField(selected, focus, current.Insert(fieldCaret, value));
        fieldCaret += value.Length;
        Changed(focus + " updated");
    }

    private static string GetField(int index, string name)
    {
        CallRecord c = calls[index];
        if (name == "CallerName") return c.CallerName;
        if (name == "Number") return c.Number;
        if (name == "Location") return c.Location;
        if (name == "Inc") return c.Inc;
        if (name == "Type") return c.Type;
        return "";
    }

    private static void SetField(int index, string name, string value)
    {
        if (name == "CallerName") calls[index].CallerName = value;
        else if (name == "Number") calls[index].Number = value;
        else if (name == "Location") calls[index].Location = value;
        else if (name == "Inc") calls[index].Inc = value;
    }

    private static void Insert(string text)
    {
        RecordEdit(new EditAction { Field = "Notes", Position = caret, Removed = "", Inserted = text });
        GetNoteBuffer(selected).Insert(caret, text);
        caret += text.Length;
        Changed("Edited call #" + GetDailyCallNumber(selected));
    }

    private static void Backspace()
    {
        if (focus != "Notes") { DeleteField(true); return; }
        if (caret <= 0) return;
        string removed = GetNoteBuffer(selected).GetRange(caret - 1, 1);
        RecordEdit(new EditAction { Field = "Notes", Position = caret - 1, Removed = removed, Inserted = "" });
        GetNoteBuffer(selected).Remove(--caret, 1);
        Changed("Edited call #" + GetDailyCallNumber(selected));
    }

    private static void Delete()
    {
        if (focus != "Notes") { DeleteField(false); return; }
        if (caret >= GetNotes(selected)) return;
        string removed = GetNoteBuffer(selected).GetRange(caret, 1);
        RecordEdit(new EditAction { Field = "Notes", Position = caret, Removed = removed, Inserted = "" });
        GetNoteBuffer(selected).Remove(caret, 1);
        Changed("Edited call #" + GetDailyCallNumber(selected));
    }

    private static void DeleteField(bool backward)
    {
        string value = GetField(selected, focus);
        if (backward && fieldCaret > 0) { RecordEdit(new EditAction { Field = focus, Position = fieldCaret - 1, Removed = value.Substring(fieldCaret - 1, 1), Inserted = "" }); value = value.Remove(--fieldCaret, 1); SetField(selected, focus, value); Changed("Edited " + focus); }
        else if (!backward && fieldCaret < value.Length) { RecordEdit(new EditAction { Field = focus, Position = fieldCaret, Removed = value.Substring(fieldCaret, 1), Inserted = "" }); value = value.Remove(fieldCaret, 1); SetField(selected, focus, value); Changed("Edited " + focus); }
    }

    private static void DeleteNoteWord(bool backward)
    {
        NoteBuffer b = GetNoteBuffer(selected);
        int start = backward ? b.WordBoundary(caret, true) : caret;
        int end = backward ? caret : b.WordBoundary(caret, false);
        if (end <= start) return;
        string removed = b.GetRange(start, end - start);
        RecordEdit(new EditAction { Field = "Notes", Position = start, Removed = removed, Inserted = "" });
        b.Remove(start, end - start);
        caret = start;
        Changed("Edited call #" + GetDailyCallNumber(selected));
    }

    private static void DeleteFieldWord(bool backward)
    {
        string value = GetField(selected, focus);
        int start = backward ? WordBoundary(value, fieldCaret, true) : fieldCaret;
        int end = backward ? fieldCaret : WordBoundary(value, fieldCaret, false);
        if (end <= start) return;
        RecordEdit(new EditAction { Field = focus, Position = start, Removed = value.Substring(start, end - start), Inserted = "" });
        SetField(selected, focus, value.Remove(start, end - start));
        fieldCaret = start;
        Changed("Edited " + focus);
    }

    private static int WordBoundary(string text, int index, bool backward)
    {
        int position = Math.Max(0, Math.Min(index, text.Length));
        if (backward)
        {
            while (position > 0 && Char.IsWhiteSpace(text[position - 1])) position--;
            while (position > 0 && !Char.IsWhiteSpace(text[position - 1])) position--;
        }
        else
        {
            while (position < text.Length && !Char.IsWhiteSpace(text[position])) position++;
            while (position < text.Length && Char.IsWhiteSpace(text[position])) position++;
        }
        return position;
    }

    private static void ScrollHistory(int delta)
    {
        if (calls.Count == 0) return;
        int height;
        try { height = Console.WindowHeight; } catch { height = 24; }
        int visible = height < 12 ? Math.Max(1, height - 5) : Math.Max(1, (height - 12) / 3);
        selected = Math.Max(0, Math.Min(calls.Count - 1, selected + delta));
        historyTop = Math.Max(0, Math.Min(calls.Count - visible, historyTop + delta));
        if (selected < historyTop) historyTop = selected;
        if (selected >= historyTop + visible) historyTop = selected - visible + 1;
        focus = "Notes";
        caret = GetNotes(selected);
        status = "History " + (historyTop + 1) + "-" + Math.Min(calls.Count, historyTop + visible);
    }

    private static void SetExplicitTypeFocus()
    {
        if (selected < 0) { status = "Create a call first"; return; }
        focus = "Type";
        status = "Type focus: Left/Right changes type; Escape returns to Notes";
    }

    private static string Clip(string text, int width)
    {
        if (String.IsNullOrEmpty(text)) return "";
        return text.Length > width ? text.Substring(0, width) : text;
    }

    private static string BoxBorder(int width)
    {
        if (width < 2) return new string('-', Math.Max(0, width));
        return "+" + new string('-', width - 2) + "+";
    }

    private static string BoxContent(string content, int width)
    {
        if (width < 2) return Clip(content, width);
        int innerWidth = width - 2;
        return "|" + Clip(content, innerWidth).PadRight(innerWidth) + "|";
    }

    private static ConsoleColor GetTextColorSetting(int index)
    {
        switch (index)
        {
            case 0: return GetThemeAccentColor();
            case 1: return GetThemeForegroundColor();
            case 2: return GetThemeLabelColor();
            case 3: return GetThemeWarningColor();
            case 4: return GetConfiguredTextColor("TextColor.Help", GetThemeMutedColor());
            case 5: return GetThemeMutedColor();
            case 6: return GetNoteHighlightColor(noteHighlightPattern.Match("KDS"));
            default: throw new ArgumentOutOfRangeException("index");
        }
    }

    // Settings and search screens share the same terminal renderer.
    private static void BuildSettingsRows(string[] rows, int width, int height)
    {
        rows[1] = " SETTINGS  (Up/Down choose; Left/Right change; Enter edits folder; Esc/F10 closes)";
        int outlineItem = Types.Length + 2;
        int textColorStart = outlineItem + 1;
        int dataDirectoryItem = textColorStart + textColorKeys.Length;
        settingsSelection = Math.Max(0, Math.Min(dataDirectoryItem, settingsSelection));

        List<int> menuEntries = new List<int>();
        menuEntries.Add(-1);
        menuEntries.Add(0);
        menuEntries.Add(1);
        menuEntries.Add(-2);
        for (int i = 0; i < Types.Length; i++) menuEntries.Add(i + 2);
        menuEntries.Add(-3);
        menuEntries.Add(outlineItem);
        menuEntries.Add(-4);
        for (int i = 0; i < textColorKeys.Length; i++) menuEntries.Add(textColorStart + i);
        menuEntries.Add(-5);
        menuEntries.Add(dataDirectoryItem);

        int selectedEntry = menuEntries.IndexOf(settingsSelection);
        int visibleItems = Math.Max(1, height - 5);
        int firstItem = Math.Max(0, selectedEntry - visibleItems + 1);
        int lastItem = Math.Min(menuEntries.Count, firstItem + visibleItems);
        for (int item = firstItem; item < lastItem; item++)
        {
            int row = 3 + item - firstItem;
            int setting = menuEntries[item];
            if (setting < 0)
            {
                string[] categories = { "GENERAL", "CALL TYPE COLORS", "CALL APPEARANCE", "TEXT COLORS", "STORAGE" };
                rows[row] = "  -- " + categories[-setting - 1] + " --";
                continue;
            }

            string prefix = settingsSelection == setting ? "> " : "  ";
            if (setting == 0)
                rows[row] = prefix + "Default call type: " + ReadDefaultCallType();
            else if (setting == 1)
                rows[row] = prefix + "Theme: " + theme;
            else if (setting < Types.Length + 2)
            {
                string callType = Types[setting - 2];
                ConsoleColor color = GetCallTypeColor(callType);
                rows[row] = prefix + callType + " color: " + color;
                settingsColorRows[row] = color;
            }
            else if (setting == outlineItem)
            {
                rows[row] = prefix + "Active call outline: " + callBoxOutlineColor + " (bold)";
                settingsColorRows[row] = GetBoldOutlineColor(callBoxOutlineColor);
            }
            else if (setting >= textColorStart && setting < dataDirectoryItem)
            {
                int colorIndex = setting - textColorStart;
                ConsoleColor color = GetTextColorSetting(colorIndex);
                rows[row] = prefix + textColorLabels[colorIndex] + " color: " + color;
                settingsColorRows[row] = color;
            }
            else
                rows[row] = prefix + "Data directory: " +
                    (String.Equals(dataDirectory, GetDefaultDataDirectory(), StringComparison.OrdinalIgnoreCase)
                        ? ".\\data" : dataDirectory);
        }
        rows[height - 2] = " Up/Down choose | Left/Right change | Enter edit folder | Esc/F10 close";
        rows[height - 1] = " " + status;
    }

    private static void BuildSearchRows(string[] rows, int width, int height)
    {
        rows[1] = " SEARCH CALLS  (case-insensitive; searches type, date, number, INC, store/location, and notes)";
        int queryWidth = Math.Max(0, width - 8);
        string visibleQuery = searchQuery.Length <= queryWidth
            ? searchQuery : searchQuery.Substring(searchQuery.Length - queryWidth, queryWidth);
        rows[3] = " Query: " + visibleQuery;
        searchCursorX = Math.Min(width - 1, 8 + visibleQuery.Length);
        searchCursorY = 3;
        if (!searchComplete)
            rows[4] = " Enter search | Esc close";
        else
        {
            rows[4] = " " + searchMatches.Count + " match(es) | Up/Down choose | Enter jump to call | R new search | Esc close";
            if (searchMatches.Count == 0)
                rows[5] = " No calls matched. Press R to change the search.";
            else
            {
                int visible = Math.Max(1, height - 8);
                searchSelection = Math.Max(0, Math.Min(searchMatches.Count - 1, searchSelection));
                int first = Math.Max(0, Math.Min(searchSelection - visible + 1, searchMatches.Count - visible));
                int last = Math.Min(searchMatches.Count, first + visible);
                for (int result = first; result < last; result++)
                {
                    CallRecord call = calls[searchMatches[result]];
                    string details = String.IsNullOrWhiteSpace(call.Inc) ? "" : " | INC " + call.Inc;
                    if (!String.IsNullOrWhiteSpace(call.Number)) details += " | Number " + call.Number;
                    if (!String.IsNullOrWhiteSpace(call.Location)) details += " | Location " + call.Location;
                    if (!String.IsNullOrWhiteSpace(call.Notes))
                    {
                        string excerpt = GetNoteBuffer(searchMatches[result]).GetRange(0, Math.Min(100, GetNotes(searchMatches[result])))
                            .Replace('\r', ' ').Replace('\n', ' ');
                        details += " | " + excerpt;
                    }
                    string date = call.StartTime;
                    DateTime parsedDate;
                    if (DateTime.TryParse(call.StartTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsedDate))
                        date = FormatCallDateTime(parsedDate);
                    string marker = result == searchSelection ? "> " : "  ";
                    rows[5 + result - first] = marker + "Call #" + GetDailyCallNumber(searchMatches[result]) +
                        " [" + call.Type + "] " + date + details;
                }
                searchCursorY = 3;
            }
        }
        rows[height - 2] = " Ctrl+F search | Up/Down browse results | Enter jump to selected call";
        rows[height - 1] = " " + status;
        if (searchComplete) searchCursorY = height - 1;
    }

    private static void OpenSearch()
    {
        searchOpen = true;
        searchComplete = false;
        searchQuery = "";
        searchMatches.Clear();
        searchSelection = 0;
        previousFrame = null;
        status = "Enter a search term";
    }

    private static void HandleSearchKey(ConsoleKeyInfo key, bool ctrl)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            searchOpen = false;
            previousFrame = null;
            status = "Search closed";
            return;
        }
        if (ctrl && key.Key == ConsoleKey.F || key.Key == ConsoleKey.R)
        {
            searchQuery = "";
            searchComplete = false;
            searchMatches.Clear();
            searchSelection = 0;
            status = "Enter a search term";
            return;
        }
        if (!searchComplete)
        {
            if (key.Key == ConsoleKey.Enter)
            {
                if (String.IsNullOrWhiteSpace(searchQuery))
                {
                    status = "Enter a search term";
                    return;
                }
                FindCalls(searchQuery);
                searchComplete = true;
                searchSelection = 0;
                status = searchMatches.Count == 0
                    ? "No calls matched: " + searchQuery
                    : searchMatches.Count + " matching call(s)";
                return;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (searchQuery.Length > 0) searchQuery = searchQuery.Substring(0, searchQuery.Length - 1);
                return;
            }
            if (ctrl && key.Key == ConsoleKey.A)
            {
                searchQuery = "";
                return;
            }
            if (!ctrl && !Char.IsControl(key.KeyChar)) searchQuery += key.KeyChar;
            return;
        }
        if (key.Key == ConsoleKey.UpArrow && searchMatches.Count > 0)
            searchSelection = Math.Max(0, searchSelection - 1);
        else if (key.Key == ConsoleKey.DownArrow && searchMatches.Count > 0)
            searchSelection = Math.Min(searchMatches.Count - 1, searchSelection + 1);
        else if (key.Key == ConsoleKey.Enter && searchMatches.Count > 0)
        {
            int callIndex = searchMatches[searchSelection];
            selected = callIndex;
            historyTop = callIndex;
            focus = "Notes";
            caret = GetNotes(selected);
            searchOpen = false;
            previousFrame = null;
            status = "Jumped to call #" + GetDailyCallNumber(selected);
        }
    }

    private static void FindCalls(string query)
    {
        searchMatches.Clear();
        // History is stored oldest-first, so scan backwards to show the newest
        // matching call at the top of the results.
        for (int i = calls.Count - 1; i >= 0; i--)
            if (CallMatches(i, query))
                searchMatches.Add(i);
    }

    private static bool CallMatches(int callIndex, string query)
    {
        CallRecord call = calls[callIndex];
        StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        if (ContainsSearchText(call.Type, query, comparison) ||
            ContainsSearchText(call.StartTime, query, comparison) ||
        ContainsSearchText(GetFormattedCallDate(call.StartTime), query, comparison) ||
        ContainsSearchText(call.EndTime, query, comparison) ||
        ContainsSearchText(GetFormattedCallDate(call.EndTime), query, comparison) ||
        ContainsSearchText(call.CallerName, query, comparison) ||
        ContainsSearchText(call.Number, query, comparison) ||
            ContainsSearchText(call.Inc, query, comparison) ||
            ContainsSearchText(call.Location, query, comparison) ||
            ContainsSearchText(call.Status, query, comparison) ||
            call.Id.ToString(CultureInfo.InvariantCulture).IndexOf(query, comparison) >= 0)
            return true;
        // Notes may be very large; NoteBuffer searches bounded chunks instead
        // of constructing a second full-sized string.
        NoteBuffer note = GetNoteBuffer(callIndex);
        return note.IndexOf(query, 0, comparison) >= 0;
    }

    private static bool ContainsSearchText(string value, string query, StringComparison comparison)
    {
        return !String.IsNullOrEmpty(value) && value.IndexOf(query, comparison) >= 0;
    }

    private static void WriteSettingsColorRow(string row, int width, ConsoleColor color)
    {
        string colorName = color.ToString();
        int colorStart = row.LastIndexOf(colorName, StringComparison.Ordinal);
        if (colorStart < 0)
        {
            Console.Write(row.PadRight(width));
            return;
        }
        Console.Write(row.Substring(0, colorStart));
        Console.ForegroundColor = color;
        Console.Write(colorName);
        Console.ForegroundColor = GetThemeForegroundColor();
        Console.Write(row.Substring(colorStart + colorName.Length));
        Console.Write(new string(' ', Math.Max(0, width - row.Length)));
    }

    // Theme-aware terminal row colors and per-call type colors.
    private static int SetRowColors(string row, int index)
    {
        int style = GetRowStyle(row, index);
        Console.ForegroundColor = (ConsoleColor)(style & 0x0F);
        Console.BackgroundColor = (ConsoleColor)((style >> 4) & 0x0F);
        return style;
    }

    private static int GetRowStyle(string row, int index)
    {
        ConsoleColor foreground = GetThemeForegroundColor();
        ConsoleColor background = ConsoleColor.Black;
        bool selectedBorder = selectedCardBorderRows.Contains(index);
        bool title = index == 0;
        bool border = row.StartsWith("+", StringComparison.Ordinal) || row.StartsWith("|", StringComparison.Ordinal);
        bool help = row.IndexOf("Ctrl+", StringComparison.OrdinalIgnoreCase) >= 0 ||
            row.StartsWith(" Up/Down", StringComparison.Ordinal) ||
            row.StartsWith(" SETTINGS", StringComparison.Ordinal);
        bool statusLine = row == " " + status;
        bool footer = row.IndexOf("Ctrl+", StringComparison.OrdinalIgnoreCase) >= 0 ||
            row.StartsWith(" ", StringComparison.Ordinal) && index > 0 &&
            (statusLine || row.StartsWith(" SETTINGS", StringComparison.Ordinal) ||
                row.StartsWith(" Up/Down", StringComparison.Ordinal));
        if (title) foreground = GetThemeAccentColor();
        else if (border) foreground = GetThemeBorderColor();
        if (selectedBorder) foreground = GetBoldOutlineColor(callBoxOutlineColor);
        if (row.IndexOf("[Finished]", StringComparison.OrdinalIgnoreCase) >= 0)
            foreground = GetThemeSuccessColor();
        else if (row.IndexOf("[Open]", StringComparison.OrdinalIgnoreCase) >= 0)
            foreground = GetThemeWarningColor();
        else if (row.IndexOf("Number:", StringComparison.OrdinalIgnoreCase) >= 0 ||
            row.IndexOf("Location:", StringComparison.OrdinalIgnoreCase) >= 0 ||
            row.IndexOf("INC:", StringComparison.OrdinalIgnoreCase) >= 0 ||
            row.IndexOf("Notes:", StringComparison.OrdinalIgnoreCase) >= 0)
            foreground = GetThemeLabelColor();
        else if (row.StartsWith("  -- ", StringComparison.Ordinal) &&
            row.EndsWith(" --", StringComparison.Ordinal))
            foreground = GetThemeAccentColor();
        else if (help)
            foreground = GetConfiguredTextColor("TextColor.Help",
                row.IndexOf("Ctrl+", StringComparison.OrdinalIgnoreCase) >= 0
                    ? GetThemeMutedColor() : GetThemeForegroundColor());
        if (footer && statusLine)
            foreground = GetConfiguredTextColor("TextColor.Status", GetThemeForegroundColor());
        return (int)foreground | ((int)background << 4);
    }

    private static void HandleSettingsKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.F10) { settingsOpen = false; status = "Settings closed"; return; }
        if (key.Key == ConsoleKey.UpArrow) settingsSelection = Math.Max(0, settingsSelection - 1);
        else if (key.Key == ConsoleKey.DownArrow)
            settingsSelection = Math.Min(Types.Length + 3 + textColorKeys.Length, settingsSelection + 1);
        else if (key.Key == ConsoleKey.LeftArrow || key.Key == ConsoleKey.RightArrow)
        {
            int direction = key.Key == ConsoleKey.LeftArrow ? -1 : 1;
            if (settingsSelection == 0)
            {
                int index = Array.IndexOf(Types, ReadDefaultCallType());
                SaveSetting("DefaultCallType", Types[(index + direction + Types.Length) % Types.Length]);
            }
            else if (settingsSelection == 1)
            {
                int index = Array.IndexOf(themes, theme);
                theme = themes[(index + direction + themes.Length) % themes.Length];
                SaveSetting("Theme", theme);
                previousFrame = null;
            }
            else if (settingsSelection == Types.Length + 2)
            {
                int index = Array.IndexOf(editableCallTypeColors, callBoxOutlineColor);
                int next = (Math.Max(0, index) + direction + editableCallTypeColors.Length) % editableCallTypeColors.Length;
                ConsoleColor color = editableCallTypeColors[next];
                if (SaveSetting("CallBoxOutlineColor", color.ToString()))
                {
                    callBoxOutlineColor = color;
                    previousFrame = null;
                }
            }
            else if (settingsSelection >= Types.Length + 3 &&
                settingsSelection < Types.Length + 3 + textColorKeys.Length)
            {
                int colorIndex = settingsSelection - (Types.Length + 3);
                int index = Array.IndexOf(editableCallTypeColors, GetTextColorSetting(colorIndex));
                int next = (Math.Max(0, index) + direction + editableCallTypeColors.Length) % editableCallTypeColors.Length;
                ConsoleColor color = editableCallTypeColors[next];
                if (SaveSetting(textColorKeys[colorIndex], color.ToString()))
                {
                    textColors[textColorKeys[colorIndex]] = color;
                    previousFrame = null;
                }
            }
            else if (settingsSelection >= 2 && settingsSelection < Types.Length + 2)
            {
                string callType = Types[settingsSelection - 2];
                ConsoleColor current = GetCallTypeColor(callType);
                int index = Array.IndexOf(editableCallTypeColors, current);
                int next = (Math.Max(0, index) + direction + editableCallTypeColors.Length) % editableCallTypeColors.Length;
                ConsoleColor color = editableCallTypeColors[next];
                if (SaveSetting("CallTypeColor." + callType, color.ToString()))
                {
                    callTypeColors[callType] = color;
                    previousFrame = null;
                }
            }
        }
        else if (key.Key == ConsoleKey.Enter &&
            settingsSelection == Types.Length + 3 + textColorKeys.Length)
        {
            Console.CursorVisible = true;
            Console.SetCursorPosition(0, Math.Max(0, Console.WindowHeight - 2));
            Console.Write(new string(' ', Math.Max(1, Console.WindowWidth - 1)));
            Console.SetCursorPosition(0, Math.Max(0, Console.WindowHeight - 2));
            Console.Write("New data directory: ");
            string path = Console.ReadLine();
            if (!String.IsNullOrWhiteSpace(path)) ChangeDataDirectory(path.Trim());
        }
    }

    // Read and write settings while preserving unknown JSON properties.
    private static void LoadTheme()
    {
        try
        {
            string settings = File.Exists(settingsFile) ? File.ReadAllText(settingsFile, Encoding.UTF8) : "{}";
            string value = ReadSettingsString(settings, "Theme");
            if (Array.IndexOf(themes, value) >= 0) theme = value;
            string outline = ReadSettingsString(settings, "CallBoxOutlineColor");
            ConsoleColor parsedOutline;
            if (Enum.TryParse<ConsoleColor>(outline, true, out parsedOutline) &&
                parsedOutline != ConsoleColor.Black && Enum.IsDefined(typeof(ConsoleColor), parsedOutline))
                callBoxOutlineColor = parsedOutline;
            textColors.Clear();
            foreach (string key in textColorKeys)
            {
                string configured = ReadSettingsString(settings, key);
                ConsoleColor parsed;
                if (Enum.TryParse<ConsoleColor>(configured, true, out parsed) &&
                    parsed != ConsoleColor.Black && Enum.IsDefined(typeof(ConsoleColor), parsed))
                    textColors[key] = parsed;
            }
            callTypeColors.Clear();
            foreach (string callType in Types)
            {
                ConsoleColor color = GetDefaultCallTypeColor(callType);
                // Per-type colors are optional; older settings files retain the
                // original palette through these defaults.
                string configured = ReadSettingsString(settings, "CallTypeColor." + callType);
                ConsoleColor parsed;
                if (Enum.TryParse<ConsoleColor>(configured, true, out parsed) &&
                    parsed != ConsoleColor.Black && Enum.IsDefined(typeof(ConsoleColor), parsed))
                    color = parsed;
                callTypeColors[callType] = color;
            }
        }
        catch { }
    }

    private static bool SaveSetting(string name, string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile));
            string json = File.Exists(settingsFile) ? File.ReadAllText(settingsFile, Encoding.UTF8) : "{}";
            // Replace only the requested key so settings added by newer
            // versions (or the user) remain intact.
            json = SetJsonString(json, name, value);
            File.WriteAllText(settingsFile, json, new UTF8Encoding(false));
            status = name + " saved";
            return true;
        }
        catch (Exception ex) { status = "Settings save failed: " + ex.Message; return false; }
    }

    private static string SetJsonString(string json, string name, string value)
    {
        string property = "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"(?:\\\\.|[^\"\\\\])*\"";
        string replacement = "\"" + name + "\":" + JsonString(value);
        if (Regex.IsMatch(json, property, RegexOptions.IgnoreCase))
            return Regex.Replace(json, property, new MatchEvaluator(delegate(Match match) { return replacement; }), RegexOptions.IgnoreCase);
        int close = json.LastIndexOf('}');
        if (close < 0) return "{\"" + name + "\":" + JsonString(value) + "}";
        string prefix = json.Substring(0, close).TrimEnd();
        string separator = prefix.EndsWith("{", StringComparison.Ordinal) ? "" : ",";
        return prefix + separator + replacement + json.Substring(close);
    }

    private static string JsonString(string value)
    {
        StringBuilder b = new StringBuilder();
        using (StringWriter writer = new StringWriter(b, CultureInfo.InvariantCulture)) JsonOutput.WriteString(writer, value);
        return b.ToString();
    }

    private static void ChangeDataDirectory(string path)
    {
        try
        {
            string next = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
            if (String.Equals(next, dataDirectory, StringComparison.OrdinalIgnoreCase)) { status = "Data directory unchanged"; return; }
            string nextFile = Path.Combine(next, "calls.json");
            if (File.Exists(nextFile))
            {
                status = "That folder already has calls.json; choose another folder";
                return;
            }
            SaveAndWait();
            Directory.CreateDirectory(next);
            if (File.Exists(dataFile)) File.Copy(dataFile, nextFile);
            else File.WriteAllText(nextFile, "[]", new UTF8Encoding(false));
            if (!SaveSetting("DataDirectory", next)) return;
            dataDirectory = next;
            dataFile = nextFile;
            nextCallId = 1;
            foreach (CallRecord call in calls) nextCallId = Math.Max(nextCallId, call.Id + 1);
            previousFrame = null;
            status = "Data directory changed";
        }
        catch (Exception ex) { status = "Data directory change failed: " + ex.Message; }
    }

    private static void ExportCall()
    {
        if (selected < 0) { status = "Select a call before exporting"; return; }
        try
        {
            string directory = Path.Combine(dataDirectory, "exports");
            Directory.CreateDirectory(directory);
            CallRecord c = calls[selected];
            DateTime started;
            if (!DateTime.TryParse(c.StartTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started)) started = DateTime.Now;
            string path = Path.Combine(directory, "call-" + GetDailyCallNumber(selected).ToString("D4", CultureInfo.InvariantCulture) +
                "-" + started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".md");
            using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("# Call #" + GetDailyCallNumber(selected));
                writer.WriteLine();
                writer.WriteLine("- **Type:** " + c.Type);
                if (!String.IsNullOrWhiteSpace(c.CallerName)) writer.WriteLine("- **Caller:** " + c.CallerName);
                writer.WriteLine("- **Number:** " + c.Number);
                writer.WriteLine("- **Location:** " + c.Location);
                writer.WriteLine("- **INC:** " + c.Inc);
                writer.WriteLine("- **Status:** " + c.Status);
                writer.WriteLine("- **Started:** " + FormatCallDateTime(started));
                DateTime ended;
                if (DateTime.TryParse(c.EndTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out ended))
                {
                    writer.WriteLine("- **Ended:** " + FormatCallDateTime(ended));
                    if (c.Status == "Finished" && ended >= started)
                        writer.WriteLine("- **Duration:** " + FormatCallDuration(ended - started));
                }
                writer.WriteLine();
                writer.WriteLine("## Notes");
                writer.WriteLine();
                NoteBuffer note = GetNoteBuffer(selected);
                for (int offset = 0; offset < note.Length; offset += 16384)
                    writer.Write(note.GetRange(offset, Math.Min(16384, note.Length - offset)));
                writer.WriteLine();
            }
            status = "Exported Markdown: " + path;
        }
        catch (Exception ex) { status = "Export failed: " + ex.Message; }
    }

    private static void CopyCallToClipboard()
    {
        if (selected < 0) { status = "Select a call before copying"; return; }
        CopyToClipboard(BuildClipboardHeader(calls[selected]), GetNoteBuffer(selected),
            "Copied call #" + GetDailyCallNumber(selected) + " to clipboard");
    }

    private static void CopyNotesToClipboard()
    {
        if (selected < 0) { status = "Select a call before copying notes"; return; }
        CopyToClipboard("", GetNoteBuffer(selected),
            "Copied notes for call #" + GetDailyCallNumber(selected) + " to clipboard");
    }

    private static void CopyToClipboard(string header, NoteBuffer note, string successMessage)
    {
        IntPtr memory = IntPtr.Zero;
        bool clipboardOpen = false;
        try
        {
            long characterCapacity = GetClipboardCharacterCount(header, note);
            ulong byteCapacity = checked((ulong)characterCapacity * sizeof(char));

            memory = GlobalAlloc(0x0002, new UIntPtr(byteCapacity));
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

            IntPtr destination = GlobalLock(memory);
            if (destination == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                WriteClipboardText(destination, header, note);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            for (int attempt = 0; attempt < 10 && !clipboardOpen; attempt++)
            {
                clipboardOpen = OpenClipboard(IntPtr.Zero);
                if (!clipboardOpen) Thread.Sleep(20);
            }
            if (!clipboardOpen) throw new Win32Exception(Marshal.GetLastWin32Error(), "The clipboard is busy.");
            if (!EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (SetClipboardData(13, memory) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // Windows owns the memory after SetClipboardData succeeds.
            memory = IntPtr.Zero;
            status = successMessage;
        }
        catch (Exception ex)
        {
            status = "Clipboard copy failed: " + ex.Message;
        }
        finally
        {
            if (clipboardOpen) CloseClipboard();
            if (memory != IntPtr.Zero) GlobalFree(memory);
        }
    }

    private static string BuildClipboardHeader(CallRecord call)
    {
        DateTime started;
        bool hasStart = DateTime.TryParse(call.StartTime, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out started);
        StringBuilder header = new StringBuilder();
        header.Append("Call #").Append(GetDailyCallNumber(call)).AppendLine();
        header.Append("Type: ").Append(call.Type).AppendLine();
        if (!String.IsNullOrWhiteSpace(call.CallerName))
            header.Append("Caller: ").Append(call.CallerName).AppendLine();
        header.Append("Number: ").Append(call.Number).AppendLine();
        header.Append("Location: ").Append(call.Location).AppendLine();
        header.Append("INC: ").Append(call.Inc).AppendLine();
        header.Append("Status: ").Append(call.Status).AppendLine();
        if (hasStart)
            header.Append("Started: ").Append(FormatCallDateTime(started)).AppendLine();
        DateTime ended;
        if (DateTime.TryParse(call.EndTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out ended))
        {
            header.Append("Ended: ").Append(FormatCallDateTime(ended)).AppendLine();
            if (call.Status == "Finished" && hasStart && ended >= started)
                header.Append("Duration: ").Append(FormatCallDuration(ended - started)).AppendLine();
        }
        header.AppendLine();
        header.AppendLine("Notes:");
        return header.ToString();
    }

    private static void WriteClipboardText(IntPtr destination, string header, NoteBuffer note)
    {
        int offset = 0;
        char[] headerCharacters = header.ToCharArray();
        Marshal.Copy(headerCharacters, 0, destination, headerCharacters.Length);
        offset += headerCharacters.Length;

        const int chunkSize = 16384;
        char previous = '\0';
        for (int start = 0; start < note.Length; start += chunkSize)
        {
            char[] chunk = note.GetRange(start, Math.Min(chunkSize, note.Length - start)).ToCharArray();
            char[] normalized = new char[chunk.Length * 2];
            int normalizedLength = 0;
            foreach (char value in chunk)
            {
                if (value == '\n' && previous != '\r')
                    normalized[normalizedLength++] = '\r';
                normalized[normalizedLength++] = value;
                previous = value;
            }
            Marshal.Copy(normalized, 0, IntPtr.Add(destination, checked(offset * sizeof(char))), normalizedLength);
            offset = checked(offset + normalizedLength);
        }

        char[] lineEnding = Environment.NewLine.ToCharArray();
        Marshal.Copy(lineEnding, 0, IntPtr.Add(destination, checked(offset * sizeof(char))), lineEnding.Length);
        offset = checked(offset + lineEnding.Length);
        Marshal.WriteInt16(destination, checked(offset * sizeof(char)), 0);
    }

    private static long GetClipboardCharacterCount(string header, NoteBuffer note)
    {
        long extraLineBreaks = 0;
        char previous = '\0';
        const int chunkSize = 16384;
        for (int start = 0; start < note.Length; start += chunkSize)
        {
            string chunk = note.GetRange(start, Math.Min(chunkSize, note.Length - start));
            foreach (char value in chunk)
            {
                if (value == '\n' && previous != '\r') extraLineBreaks++;
                previous = value;
            }
        }
        return (long)header.Length + note.Length + extraLineBreaks + Environment.NewLine.Length + 1;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    private static void RecordEdit(EditAction action)
    {
        int id = calls[selected].Id;
        List<EditAction> undo;
        if (!Undo.TryGetValue(id, out undo)) { undo = new List<EditAction>(); Undo.Add(id, undo); }
        undo.Add(action);
        if (undo.Count > 500) undo.RemoveAt(0);
        List<EditAction> redo;
        if (Redo.TryGetValue(id, out redo)) redo.Clear();
    }

    private static void UndoEdit()
    {
        ApplyHistory(true);
    }

    private static void RedoEdit()
    {
        ApplyHistory(false);
    }

    private static void ApplyHistory(bool undo)
    {
        if (selected < 0) return;
        int id = calls[selected].Id;
        Dictionary<int, List<EditAction>> from = undo ? Undo : Redo;
        Dictionary<int, List<EditAction>> to = undo ? Redo : Undo;
        List<EditAction> source;
        if (!from.TryGetValue(id, out source) || source.Count == 0) { status = undo ? "Nothing to undo" : "Nothing to redo"; return; }
        EditAction action = source[source.Count - 1];
        source.RemoveAt(source.Count - 1);
        List<EditAction> destination;
        if (!to.TryGetValue(id, out destination)) { destination = new List<EditAction>(); to.Add(id, destination); }
        destination.Add(action);
        string remove = undo ? action.Inserted : action.Removed;
        string insert = undo ? action.Removed : action.Inserted;
        if (action.Field == "Notes")
        {
            NoteBuffer b = GetNoteBuffer(selected);
            if (remove.Length > 0) b.Remove(action.Position, remove.Length);
            if (insert.Length > 0) b.Insert(action.Position, insert);
            caret = action.Position + insert.Length;
        }
        else
        {
            string value = GetField(selected, action.Field);
            value = value.Remove(action.Position, remove.Length).Insert(action.Position, insert);
            SetField(selected, action.Field, value);
            focus = action.Field;
            fieldCaret = action.Position + insert.Length;
        }
        Changed(undo ? "Undid last edit" : "Redid last edit");
    }

    private static int LineStart(int pos)
    {
        NoteBuffer b = GetNoteBuffer(selected);
        while (pos > 0 && b.GetCharAt(pos - 1) != '\n') pos--;
        return pos;
    }

    private static int LineEnd(int pos)
    {
        NoteBuffer b = GetNoteBuffer(selected);
        while (pos < b.Length && b.GetCharAt(pos) != '\n') pos++;
        return pos;
    }

    private static void MoveVertical(int delta)
    {
        if (selected < 0) return;
        NoteBuffer b = GetNoteBuffer(selected);
        int direction = delta < 0 ? -1 : 1;
        for (int step = 0; step < Math.Abs(delta); step++)
        {
            int start = LineStart(caret);
            int column = caret - start;
            if (direction < 0)
            {
                if (start == 0) { caret = 0; break; }
                int previousEnd = start - 1;
                int previousStart = LineStart(previousEnd);
                caret = Math.Min(previousStart + column, previousEnd);
            }
            else
            {
                int end = LineEnd(caret);
                if (end >= b.Length) { caret = b.Length; break; }
                int nextStart = end + 1;
                int nextEnd = LineEnd(nextStart);
                caret = Math.Min(nextStart + column, nextEnd);
            }
        }
    }

    private static void FinishCall()
    {
        if (selected < 0) return;
        CallRecord c = calls[selected];
        c.Status = "Finished";
        c.EndTime = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
        Changed("Finished call #" + GetDailyCallNumber(selected));
    }

    private static void RequestDeleteCall()
    {
        if (selected < 0 || selected >= calls.Count)
        {
            status = "Select a call before deleting";
            return;
        }
        deleteConfirmation = true;
        status = "Delete call #" + GetDailyCallNumber(selected) + "? Press Y to confirm, N or Esc to cancel";
    }

    private static void HandleDeleteConfirmationKey(ConsoleKey key)
    {
        if (key == ConsoleKey.Y) ConfirmDeleteCall();
        else if (key == ConsoleKey.N || key == ConsoleKey.Escape)
        {
            deleteConfirmation = false;
            status = "Call deletion cancelled";
        }
    }

    private static void ConfirmDeleteCall()
    {
        if (!deleteConfirmation) return;
        deleteConfirmation = false;
        if (selected < 0 || selected >= calls.Count)
        {
            status = "No call selected; nothing deleted";
            return;
        }
        int deletedId = calls[selected].Id;
        int deletedCallNumber = GetDailyCallNumber(selected);
        calls.RemoveAt(selected);
        NoteBuffers.Remove(deletedId);
        Undo.Remove(deletedId);
        Redo.Remove(deletedId);
        selected = calls.Count == 0 ? -1 : Math.Min(selected, calls.Count - 1);
        focus = "Notes";
        caret = selected >= 0 ? GetNotes(selected) : 0;
        fieldCaret = 0;
        historyTop = Math.Max(0, Math.Min(historyTop, Math.Max(0, calls.Count - 1)));
        Changed("Deleted call #" + deletedCallNumber);
    }

    private static void Changed(string message)
    {
        version++;
        saveAt = DateTime.UtcNow.AddMilliseconds(1500);
        status = message;
    }

    // Autosave and history persistence.
    private static void StartSave()
    {
        if (saveTask != null && !saveTask.IsCompleted) return;
        CompleteSaveIfReady();
        Directory.CreateDirectory(dataDirectory);
        // Capture every field and note-piece list before the worker starts so
        // concurrent typing cannot change the contents midway through a save.
        int snapshotVersion = version;
        CallSnapshot[] snapshot = new CallSnapshot[calls.Count];
        for (int i = 0; i < calls.Count; i++)
        {
            CallRecord c = calls[i];
            NoteBuffer notes;
            NoteBuffers.TryGetValue(c.Id, out notes);
            snapshot[i] = new CallSnapshot {
                Id = c.Id, StartTime = c.StartTime, EndTime = c.EndTime,
                CallerName = c.CallerName, Location = c.Location, Number = c.Number, Inc = c.Inc,
                Type = c.Type, Status = c.Status,
                Notes = c.Notes, Note = notes == null ? null : notes.GetSnapshot()
            };
        }
        string temporary = dataFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        saveError = null;
        saveTask = Task.Factory.StartNew(() =>
        {
            try
            {
                CallWriter.Write(temporary, snapshot);
                // Replace only after the full JSON file has been written.
                if (File.Exists(dataFile)) File.Replace(temporary, dataFile, null);
                else File.Move(temporary, dataFile);
            }
            catch
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                throw;
            }
        });
        savedVersion = snapshotVersion;
    }

    private static void CompleteSaveIfReady()
    {
        if (saveTask == null || !saveTask.IsCompleted) return;
        try
        {
            saveTask.Wait();
            if (version == savedVersion) status = "Saved " + calls.Count + " calls";
        }
        catch (Exception ex) { saveError = ex; status = "Save failed: " + ex.GetBaseException().Message; }
        finally { saveTask = null; }
    }

    private static void SaveAndWait()
    {
        do
        {
            CompleteSaveIfReady();
            if (version != savedVersion && (saveTask == null || saveTask.IsCompleted))
                StartSave();
            if (saveTask != null && !saveTask.IsCompleted) { saveTask.Wait(); CompleteSaveIfReady(); }
        } while (version != savedVersion || (saveTask != null && !saveTask.IsCompleted));
        if (saveError != null) throw new IOException("Final save failed.", saveError);
    }

    private static int RunRoundTripTest()
    {
        // Tests use a unique temporary history file and remove it in finally;
        // no user call-history or settings file is read or changed here.
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            ".callnotes-roundtrip-" + Guid.NewGuid().ToString("N") + ".json");
        System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string settingsProbe = SetJsonString("{\"KeepMe\":true,\"Theme\":\"old\"}", "Theme", "Nord");
            if (!settingsProbe.Contains("\"KeepMe\":true") || ReadSettingsString(settingsProbe, "Theme") != "Nord")
                throw new InvalidDataException("Settings update self-test failed to preserve an unrelated setting.");
            string pathProbe = SetJsonString(settingsProbe, "DataDirectory", "C:\\Notes \"Archive\"");
            if (ReadSettingsString(pathProbe, "DataDirectory") != "C:\\Notes \"Archive\"" ||
                !pathProbe.Contains("\"KeepMe\":true"))
                throw new InvalidDataException("Settings path escaping/preservation self-test failed.");
            string colorProbe = SetJsonString(pathProbe, "CallTypeColor.Support", "Cyan");
            if (ReadSettingsString(colorProbe, "CallTypeColor.Support") != "Cyan" ||
                !colorProbe.Contains("\"KeepMe\":true"))
                throw new InvalidDataException("Call type color setting persistence self-test failed.");
            string outlineProbe = SetJsonString(colorProbe, "CallBoxOutlineColor", "DarkGreen");
            if (ReadSettingsString(outlineProbe, "CallBoxOutlineColor") != "DarkGreen" ||
                !outlineProbe.Contains("\"KeepMe\":true"))
                throw new InvalidDataException("Active call outline setting persistence self-test failed.");
            string textColorProbe = SetJsonString(outlineProbe, "TextColor.Heading", "Cyan");
            textColorProbe = SetJsonString(textColorProbe, "TextColor.Status", "Yellow");
            if (ReadSettingsString(textColorProbe, "TextColor.Heading") != "Cyan" ||
                ReadSettingsString(textColorProbe, "TextColor.Status") != "Yellow" ||
                !textColorProbe.Contains("\"KeepMe\":true"))
                throw new InvalidDataException("Text color settings persistence self-test failed.");
            string markdownSample = @"\ / : . , < > ==== ---- KDS kds piks PIKS pos POS *DPOS dpos MSR msr ped PED datto DATTO *Store sn tn inc dns DNS 1 2 3 4 5 6 7 8 9 0 123456789 2e12e1e (qweqw) () {} [] [couldn't hear anything] (this was the fix)";
            Dictionary<string, ConsoleColor> expectedHighlights = new Dictionary<string, ConsoleColor>(StringComparer.OrdinalIgnoreCase)
            {
                { "KDS", ConsoleColor.Magenta },
                { "DPOS", ConsoleColor.Blue }, { "MSR", ConsoleColor.Blue },
                { "123456789", ConsoleColor.Magenta },
                { "(qweqw)", ConsoleColor.Green }, { "()", ConsoleColor.Green },
                { "====", ConsoleColor.DarkGray }, { "----", ConsoleColor.DarkGray },
                { "/", ConsoleColor.DarkCyan }, { "<", ConsoleColor.DarkCyan },
                { "*", ConsoleColor.DarkGray }, { "{", ConsoleColor.DarkGray },
                { "}", ConsoleColor.DarkGray }, { "[couldn't hear anything]", ConsoleColor.Magenta }
            };
            HashSet<string> foundHighlights = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in noteHighlightPattern.Matches(markdownSample))
            {
                ConsoleColor expectedColor;
                if (expectedHighlights.TryGetValue(match.Value, out expectedColor))
                {
                    if (GetNoteHighlightColor(match) != expectedColor)
                        throw new InvalidDataException("Markdown note color mapping self-test failed for " + match.Value + ".");
                    foundHighlights.Add(match.Value);
                }
            }
            if (foundHighlights.Count != expectedHighlights.Count)
                throw new InvalidDataException("Markdown note highlighting missed expected sample tokens.");
            string special = "quotes \" slash \\ newline\n tab\t unicode café 🙂 control \u0001";
            string source = "start\n" + new string('x', 6 * 1024 * 1024) + special + "\nend";
            NoteBuffer note = new NoteBuffer(source);
            const int insertion = 3145728;
            string edit = "insert \"quoted\" \\path\n\tλ";
            note.Insert(insertion, edit);
            note.Remove(insertion + edit.Length, 7);
            string expected = source.Insert(insertion, edit).Remove(insertion + edit.Length, 7);
            if (note.IndexOf("QUOTED", 0, StringComparison.OrdinalIgnoreCase) != expected.IndexOf("quoted", StringComparison.Ordinal))
                throw new InvalidDataException("Chunked note search self-test failed.");
            NoteBuffer.Snapshot immutable = note.GetSnapshot();
            note.Insert(0, "later mutation must not affect the captured snapshot ");
            CallSnapshot snapshot = new CallSnapshot {
                Id = 42, StartTime = "2026-09-29T12:34:56.0000000+01:00", EndTime = "",
                CallerName = "Caller \"Name\" \\ West", Location = "Retail \"A\" \\ West \u0001",
                Number = "0012\t\\", Inc = "INC0000123\n",
                Type = "Support", Status = "Open", Notes = "", Note = immutable
            };
            CallWriter.Write(path, new[] { snapshot });
            List<CallRecord> roundTrip = CallHistory.Read(path);
            if (roundTrip.Count != 1 || roundTrip[0].Id != 42 ||
                roundTrip[0].CallerName != snapshot.CallerName ||
                roundTrip[0].Location != snapshot.Location ||
                roundTrip[0].Number != snapshot.Number ||
                roundTrip[0].Inc != snapshot.Inc ||
                roundTrip[0].Type != snapshot.Type ||
                roundTrip[0].Notes != expected)
                throw new InvalidDataException("Large-note JSON round-trip did not preserve call data.");
            string legacyJson = File.ReadAllText(path, Encoding.UTF8).Replace("\"Location\":", "\"StoreName\":");
            File.WriteAllText(path, legacyJson, new UTF8Encoding(false));
            List<CallRecord> legacyRoundTrip = CallHistory.Read(path);
            if (legacyRoundTrip.Count != 1 || legacyRoundTrip[0].Location != snapshot.Location)
                throw new InvalidDataException("Legacy location field migration self-test failed.");

            List<CallRecord> oldCalls = calls;
            int oldSelected = selected, oldCaret = caret, oldHistoryTop = historyTop;
            try
            {
                calls = new List<CallRecord>();
                DateTime countTestDay = DateTime.Today;
                List<CallRecord> countTestCalls = new List<CallRecord> {
                    new CallRecord { StartTime = countTestDay.AddHours(9).ToString("o", CultureInfo.InvariantCulture) },
                    new CallRecord { StartTime = countTestDay.AddHours(16).ToString("o", CultureInfo.InvariantCulture) },
                    new CallRecord { StartTime = countTestDay.AddDays(-1).ToString("o", CultureInfo.InvariantCulture) },
                    new CallRecord { StartTime = "not-a-date" }
                };
                if (CountCallsForDate(countTestCalls, countTestDay) != 2 ||
                    CountCallsForDate(countTestCalls, countTestDay.AddDays(-1)) != 1)
                    throw new InvalidDataException("Daily call-count self-test failed.");
                calls.Add(new CallRecord { Id = 95, StartTime = countTestDay.AddHours(9).ToString("o", CultureInfo.InvariantCulture) });
                calls.Add(new CallRecord { Id = 96, StartTime = countTestDay.AddDays(-1).ToString("o", CultureInfo.InvariantCulture) });
                calls.Add(new CallRecord { Id = 97, StartTime = countTestDay.AddHours(16).ToString("o", CultureInfo.InvariantCulture) });
                if (GetDailyCallNumber(0) != 1 || GetDailyCallNumber(1) != 1 || GetDailyCallNumber(2) != 2)
                    throw new InvalidDataException("Per-day visible call numbering self-test failed.");
                calls.Clear();
                for (int i = 1; i <= 3; i++)
                    calls.Add(new CallRecord { Id = i, Type = i == 2 ? "Internal" : "Support", Status = i == 1 ? "Finished" : "Open",
                        StartTime = "2026-01-01T00:00:00", EndTime = i == 1 ? "2026-01-01T00:02:05" : "",
                        Number = i.ToString(CultureInfo.InvariantCulture),
                        CallerName = i == 2 ? "Alice Example" : "Taylor Caller", Inc = i == 2 ? "INC-2002" : "",
                        Location = i == 2 ? "Example Location" : "",
                        Notes = i == 1 ? "sample note\nsecond line" : "sample note" });
                if (FormatCallDateTime(new DateTime(2026, 1, 1, 0, 0, 0)) != "01 Jan 2026 00:00" ||
                    FormatCallDuration(TimeSpan.FromSeconds(65)) != "1m 05s" ||
                    FormatCallDuration(TimeSpan.FromSeconds(3723)) != "1h 02m 03s" ||
                    FormatCallDuration(TimeSpan.FromDays(1).Add(TimeSpan.FromMinutes(2))) != "1d 00h 02m" ||
                    !BuildCallTiming(calls[0]).Contains("Duration: 2m 05s") ||
                    BuildCallTiming(calls[1]).Contains("Duration:"))
                    throw new InvalidDataException("Readable call time and duration formatting self-test failed.");
                selected = 1;
                caret = 10000;
                if (GetField(1, "CallerName") != "Alice Example")
                    throw new InvalidDataException("Caller name field read self-test failed.");
                SetField(1, "CallerName", "Alex Example");
                if (GetField(1, "CallerName") != "Alex Example")
                    throw new InvalidDataException("Caller name field edit self-test failed.");
                SetField(1, "CallerName", "Alice Example");
                string originalFocus = focus;
                int originalFieldCaret = fieldCaret;
                focus = "Number";
                CycleField();
                if (focus != "Location")
                    throw new InvalidDataException("Location field order self-test failed.");
                CycleField();
                if (focus != "CallerName")
                    throw new InvalidDataException("Caller-name field order self-test failed.");
                CycleField();
                if (focus != "Inc")
                    throw new InvalidDataException("INC field order self-test failed.");
                CycleField();
                if (focus != "Notes")
                    throw new InvalidDataException("Notes field order self-test failed.");
                focus = originalFocus;
                fieldCaret = originalFieldCaret;
                FindCalls("alice example");
                if (searchMatches.Count != 1 || searchMatches[0] != 1)
                    throw new InvalidDataException("Caller name search self-test failed.");
                string clipboardHeader = BuildClipboardHeader(calls[0]);
                if (clipboardHeader.IndexOf("Caller: Taylor Caller", StringComparison.Ordinal) < 0)
                    throw new InvalidDataException("Caller name whole-call clipboard self-test failed.");
                NoteBuffer clipboardNote = GetNoteBuffer(0);
                IntPtr clipboardTestMemory = Marshal.AllocHGlobal(
                    checked((int)(GetClipboardCharacterCount(clipboardHeader, clipboardNote) * sizeof(char))));
                try
                {
                    WriteClipboardText(clipboardTestMemory, clipboardHeader, clipboardNote);
                    string clipboardText = Marshal.PtrToStringUni(clipboardTestMemory);
                    if (clipboardText == null || clipboardText.IndexOf('|') >= 0 ||
                        clipboardText.IndexOf("Call #1", StringComparison.Ordinal) < 0 ||
                        clipboardText.IndexOf("Number: 1", StringComparison.Ordinal) < 0 ||
                        clipboardText.IndexOf("Location: ", StringComparison.Ordinal) < 0 ||
                        clipboardText.IndexOf("Notes:" + Environment.NewLine + "sample note" +
                            Environment.NewLine + "second line" + Environment.NewLine, StringComparison.Ordinal) < 0)
                        throw new InvalidDataException("Plain-text clipboard formatting self-test failed.");
                }
                finally
                {
                    Marshal.FreeHGlobal(clipboardTestMemory);
                }
                IntPtr notesOnlyMemory = Marshal.AllocHGlobal(
                    checked((int)(GetClipboardCharacterCount("", clipboardNote) * sizeof(char))));
                try
                {
                    WriteClipboardText(notesOnlyMemory, "", clipboardNote);
                    string notesOnlyText = Marshal.PtrToStringUni(notesOnlyMemory);
                    if (notesOnlyText != "sample note" + Environment.NewLine + "second line" + Environment.NewLine ||
                        notesOnlyText.IndexOf('|') >= 0 || notesOnlyText.IndexOf("Call #", StringComparison.Ordinal) >= 0)
                        throw new InvalidDataException("Notes-only clipboard export self-test failed.");
                }
                finally
                {
                    Marshal.FreeHGlobal(notesOnlyMemory);
                }
                string[] normalSize = new string[32];
                for (int row = 0; row < normalSize.Length; row++) normalSize[row] = "";
                selectedCardRows.Clear();
                selectedCardBorderRows.Clear();
                BuildCallRows(normalSize, 80, normalSize.Length);
                if (!normalSize[1].Contains("Selected #2") || !normalSize[2].StartsWith("+") ||
                    !normalSize[3].Contains("Call #1") || !normalSize[5].Contains("Duration: 2m 05s") ||
                    !normalSize[6].StartsWith("|", StringComparison.Ordinal) ||
                    normalSize[6].Trim('|', ' ') != "" ||
                    !normalSize[8].StartsWith("+") ||
                    normalSize[9] != "" || !normalSize[10].StartsWith("+") ||
                    !normalSize[11].Contains("Call #2") ||
                    !normalSize[12].Contains("Caller: Alice Example") ||
                    !normalSize[13].Contains("Started: 01 Jan 2026 00:00") ||
                    !normalSize[15].StartsWith("|") || !normalSize[15].Contains("sample note") ||
                    !normalSize[16].StartsWith("+") || normalSize[17] != "" ||
                    !normalSize[18].StartsWith("+") || !normalSize[19].Contains("Call #3") ||
                    !finishedNoteRows.Contains(7) || finishedNoteRows.Contains(15) ||
                    !normalSize[24].StartsWith("+") || normalSize[25] != "" ||
                    !normalSize[29].Contains("Ctrl+1") || !normalSize[30].Contains("Ctrl+Z") ||
                    Array.Exists(normalSize, delegate(string row) { return row.Contains("NOTES EDITOR"); }))
                    throw new InvalidDataException("Multi-card rendering/footer self-test failed.");
                string originalFieldFocus = focus;
                int originalFieldPosition = fieldCaret;
                string[] cursorRows = new string[32];
                for (int row = 0; row < cursorRows.Length; row++) cursorRows[row] = "";
                focus = "CallerName"; fieldCaret = 0;
                BuildCallRows(cursorRows, 80, cursorRows.Length);
                if (cursorY != 12 || cursorX != cursorRows[12].IndexOf("Alice Example", StringComparison.Ordinal))
                    throw new InvalidDataException("Caller-name cursor alignment self-test failed.");
                focus = "Location"; fieldCaret = 0;
                BuildCallRows(cursorRows, 80, cursorRows.Length);
                if (cursorX != cursorRows[12].IndexOf("Example Location", StringComparison.Ordinal))
                    throw new InvalidDataException("Location cursor alignment self-test failed.");
                focus = "Number"; fieldCaret = 0;
                BuildCallRows(cursorRows, 80, cursorRows.Length);
                if (cursorX != cursorRows[12].IndexOf("Number: 2", StringComparison.Ordinal) + "Number: ".Length)
                    throw new InvalidDataException("Number cursor alignment self-test failed.");
                focus = "Inc"; fieldCaret = 0;
                BuildCallRows(cursorRows, 80, cursorRows.Length);
                if (cursorX != cursorRows[12].IndexOf("INC: ", StringComparison.Ordinal) + "INC: ".Length)
                    throw new InvalidDataException("INC cursor alignment self-test failed.");
                focus = "Type"; fieldCaret = 0;
                BuildCallRows(cursorRows, 80, cursorRows.Length);
                if (cursorY != 11 || cursorX != cursorRows[11].IndexOf("Internal", StringComparison.Ordinal))
                    throw new InvalidDataException("Call-type cursor alignment self-test failed.");
                focus = originalFieldFocus;
                fieldCaret = originalFieldPosition;
                selectedCardRows.Clear();
                selectedCardBorderRows.Clear();
                BuildCallRows(normalSize, 80, normalSize.Length);
                FindCalls("internal");
                if (searchMatches.Count != 1 || searchMatches[0] != 1)
                    throw new InvalidDataException("Call type search self-test failed.");
                FindCalls("inc-2002");
                if (searchMatches.Count != 1 || searchMatches[0] != 1)
                    throw new InvalidDataException("INC number search self-test failed.");
                FindCalls("01 jan 2026 00:00");
                if (searchMatches.Count != 3)
                    throw new InvalidDataException("Formatted call date search self-test failed.");
                FindCalls("sample note");
                if (searchMatches.Count != 3 || searchMatches[0] != 2 || searchMatches[2] != 0)
                    throw new InvalidDataException("Note search/newest-first results self-test failed.");
                int previousSearchSelected = selected;
                string previousSearchFocus = focus;
                int previousSearchCaret = caret;
                int previousSearchHistoryTop = historyTop;
                searchOpen = true;
                searchComplete = true;
                searchSelection = 0;
                HandleSearchKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), false);
                if (searchOpen || selected != 2 || status != "Jumped to call #3")
                    throw new InvalidDataException("Jump to search result self-test failed.");
                selected = previousSearchSelected;
                focus = previousSearchFocus;
                caret = previousSearchCaret;
                historyTop = previousSearchHistoryTop;
                int selectedStyle = GetRowStyle(normalSize[11], 11);
                int selectedBorderStyle = GetRowStyle(normalSize[10], 10);
                int otherBorderStyle = GetRowStyle(normalSize[2], 2);
                if (((selectedStyle >> 4) & 0x0F) != (int)ConsoleColor.Black ||
                    (selectedBorderStyle & 0x0F) != (int)GetBoldOutlineColor(callBoxOutlineColor) ||
                    (otherBorderStyle & 0x0F) != (int)ConsoleColor.DarkGray)
                    throw new InvalidDataException("Reference theme selected-card palette self-test failed.");
                ConsoleColor originalOutlineColor = callBoxOutlineColor;
                try
                {
                    callBoxOutlineColor = ConsoleColor.DarkGreen;
                    if (GetBoldOutlineColor(callBoxOutlineColor) != ConsoleColor.Green ||
                        (GetRowStyle(normalSize[10], 10) & 0x0F) != (int)ConsoleColor.Green ||
                        (GetRowStyle(normalSize[2], 2) & 0x0F) != (int)ConsoleColor.DarkGray)
                        throw new InvalidDataException("Active outline color/boldness self-test failed.");
                    string[] expectedThemes = {
                        "Reference", "Campbell", "Nord", "One Half Dark",
                        "Solarized Dark", "Tango Dark", "Vintage", "Monochrome"
                    };
                    foreach (string expectedTheme in expectedThemes)
                    {
                        if (Array.IndexOf(themes, expectedTheme) < 0)
                            throw new InvalidDataException("Theme option missing: " + expectedTheme);
                        theme = expectedTheme;
                        if (GetThemeBorderColor() == ConsoleColor.Black ||
                            GetThemeAccentColor() == ConsoleColor.Black ||
                            GetThemeLabelColor() == ConsoleColor.Black)
                            throw new InvalidDataException("Theme palette contains an unusable black foreground: " + expectedTheme);
                    }
                }
                finally
                {
                    callBoxOutlineColor = originalOutlineColor;
                    theme = "Reference";
                }
                CallRecord finishedFixture = calls[0];
                if (GetHeadingColor("Support", finishedFixture) != ConsoleColor.DarkCyan ||
                    GetHeadingColor("[Finished]", finishedFixture) != ConsoleColor.DarkGreen ||
                    GetDetailLabelColor("Caller:", finishedFixture) != ConsoleColor.DarkCyan ||
                    GetDetailValueColor("Caller:", finishedFixture) != ConsoleColor.Gray ||
                    GetDetailLabelColor("Number:", finishedFixture) != ConsoleColor.DarkCyan ||
                    GetDetailValueColor("Number:", finishedFixture) != ConsoleColor.DarkYellow ||
                    !noteTextByRow.ContainsKey(7) || !noteTextByRow[7].StartsWith("Notes: ", StringComparison.Ordinal))
                    throw new InvalidDataException("Finished-call styling self-test failed: type=" +
                        GetHeadingColor("Support", finishedFixture) +
                        ", status=" + GetHeadingColor("[Finished]", finishedFixture) + ", numberLabel=" +
                        GetDetailLabelColor("Number:", finishedFixture) + ", numberValue=" +
                        GetDetailValueColor("Number:", finishedFixture) + ", noteRow=" +
                        (noteTextByRow.ContainsKey(5) ? noteTextByRow[5] : "(missing)"));
                string originalTheme = theme;
                try
                {
                    theme = "Reference";
                    string[] callTypes = { "Support", "Internal", "Other" };
                    HashSet<ConsoleColor> typeColors = new HashSet<ConsoleColor>();
                    foreach (string callType in callTypes)
                    {
                        ConsoleColor color = GetHeadingColor(callType, new CallRecord { Type = callType });
                        if (!typeColors.Add(color))
                            throw new InvalidDataException("Call type color self-test found a duplicate for " + callType + ".");
                    }
                    if (typeColors.Count != callTypes.Length)
                        throw new InvalidDataException("Call type color self-test did not assign a unique color to every type.");
                    ConsoleColor originalSupportColor = callTypeColors.ContainsKey("Support")
                        ? callTypeColors["Support"] : GetDefaultCallTypeColor("Support");
                    callTypeColors["Support"] = ConsoleColor.Cyan;
                    if (GetCallTypeColor("Support") != ConsoleColor.Cyan)
                        throw new InvalidDataException("Call type color override self-test failed.");
                    callTypeColors["Support"] = originalSupportColor;
                    Dictionary<string, ConsoleColor> originalTextColors =
                        new Dictionary<string, ConsoleColor>(textColors, StringComparer.OrdinalIgnoreCase);
                    string originalStatus = status;
                    int originalSelection = settingsSelection;
                    try
                    {
                        textColors.Clear();
                        textColors["TextColor.Heading"] = ConsoleColor.Cyan;
                        textColors["TextColor.Body"] = ConsoleColor.White;
                        textColors["TextColor.Label"] = ConsoleColor.Yellow;
                        textColors["TextColor.Status"] = ConsoleColor.Green;
                        textColors["TextColor.Help"] = ConsoleColor.Magenta;
                        textColors["TextColor.Muted"] = ConsoleColor.Blue;
                        textColors["TextColor.NoteHighlight"] = ConsoleColor.Red;
                        status = "Color preview";
                        if (GetThemeAccentColor() != ConsoleColor.Cyan ||
                            GetThemeForegroundColor() != ConsoleColor.White ||
                            GetDetailLabelColor("Caller:", finishedFixture) != ConsoleColor.Yellow ||
                            GetDetailValueColor("Number:", finishedFixture) != ConsoleColor.White ||
                            GetThemeWarningColor() != ConsoleColor.Green ||
                            GetThemeMutedColor() != ConsoleColor.Blue ||
                            GetNoteHighlightColor(noteHighlightPattern.Match("KDS")) != ConsoleColor.Red ||
                            (GetRowStyle(" Ctrl+N create call", 28) & 0x0F) != (int)ConsoleColor.Magenta ||
                            (GetRowStyle(" Color preview", 29) & 0x0F) != (int)ConsoleColor.Green)
                            throw new InvalidDataException("Configurable interface text color self-test failed.");
                        settingsSelection = Types.Length + 3;
                        string[] textSettingsRows = new string[24];
                        for (int row = 0; row < textSettingsRows.Length; row++) textSettingsRows[row] = "";
                        BuildSettingsRows(textSettingsRows, 80, textSettingsRows.Length);
                        bool foundHeadingColor = false;
                        bool foundTextCategory = false;
                        for (int row = 0; row < textSettingsRows.Length; row++)
                        {
                            if (textSettingsRows[row].Contains("-- TEXT COLORS --"))
                                foundTextCategory = true;
                            if (textSettingsRows[row].Contains("Heading text color: Cyan"))
                            {
                                foundHeadingColor = true;
                                if (!settingsColorRows.ContainsKey(row) ||
                                    settingsColorRows[row] != ConsoleColor.Cyan)
                                    throw new InvalidDataException("Heading text color preview self-test failed.");
                            }
                        }
                        if (!foundHeadingColor || !foundTextCategory)
                            throw new InvalidDataException("Categorized text color settings are missing.");
                    }
                    finally
                    {
                        textColors.Clear();
                        foreach (KeyValuePair<string, ConsoleColor> entry in originalTextColors)
                            textColors[entry.Key] = entry.Value;
                        status = originalStatus;
                        settingsSelection = originalSelection;
                        settingsColorRows.Clear();
                    }
                    int originalSettingsSelection = settingsSelection;
                    try
                    {
                        settingsSelection = 2;
                        string[] settingsRows = new string[24];
                        for (int row = 0; row < settingsRows.Length; row++) settingsRows[row] = "";
                        BuildSettingsRows(settingsRows, 80, settingsRows.Length);
                        for (int row = 0; row < settingsRows.Length; row++)
                            if (!settingsRows[row].Contains("Internal color:") &&
                                !settingsRows[row].Contains("Support color:"))
                                continue;
                            else if (!settingsColorRows.ContainsKey(row))
                                throw new InvalidDataException("Call type color settings preview self-test failed.");
                        if (!Array.Exists(settingsRows, delegate(string row) { return row.Contains("Internal color:"); }) ||
                            !Array.Exists(settingsRows, delegate(string row) { return row.Contains("Support color:"); }))
                            throw new InvalidDataException("Call type color settings controls are missing.");
                    }
                    finally
                    {
                        settingsSelection = originalSettingsSelection;
                        settingsColorRows.Clear();
                    }
                    theme = "Monochrome";
                    if (GetHeadingColor("Support", new CallRecord { Type = "Support" }) != ConsoleColor.Gray)
                        throw new InvalidDataException("Monochrome call type color self-test failed.");
                }
                finally
                {
                    theme = originalTheme;
                }
                NoteBuffer previousSelectedNote = NoteBuffers[2];
                StringBuilder manyLines = new StringBuilder();
                for (int line = 1; line <= 7; line++)
                {
                    if (line > 1) manyLines.Append('\n');
                    manyLines.Append("line ").Append(line.ToString(CultureInfo.InvariantCulture));
                }
                NoteBuffers[2] = new NoteBuffer(manyLines.ToString());
                int previousCaretForMultiline = caret;
                caret = manyLines.Length;
                string[] multiline = new string[32];
                for (int row = 0; row < multiline.Length; row++) multiline[row] = "";
                selectedCardRows.Clear();
                selectedCardBorderRows.Clear();
                BuildCallRows(multiline, 80, multiline.Length);
                int selectedHeader = Array.FindIndex(multiline, delegate(string row) { return row.StartsWith("|> Call #2", StringComparison.Ordinal); });
                int firstNoteRow = selectedHeader + 4;
                if (selectedHeader < 0 || cursorY != firstNoteRow + 6 ||
                    !multiline[cursorY].Contains("line 7") ||
                    !multiline[firstNoteRow + 7].StartsWith("+") ||
                    multiline[firstNoteRow + 8] != "")
                    throw new InvalidDataException("Growing multiline note card/caret self-test failed.");
                manyLines.Clear();
                for (int line = 1; line <= 24; line++)
                {
                    if (line > 1) manyLines.Append('\n');
                    manyLines.Append("line ").Append(line.ToString(CultureInfo.InvariantCulture));
                }
                NoteBuffers[2] = new NoteBuffer(manyLines.ToString());
                caret = manyLines.Length;
                string[] fullHeightCard = new string[32];
                for (int row = 0; row < fullHeightCard.Length; row++) fullHeightCard[row] = "";
                selectedCardRows.Clear();
                selectedCardBorderRows.Clear();
                BuildCallRows(fullHeightCard, 80, fullHeightCard.Length);
                int fullHeightHeader = Array.FindIndex(fullHeightCard, delegate(string row) { return row.StartsWith("|> Call #2", StringComparison.Ordinal); });
                int fullHeightNotes = fullHeightHeader + 4;
                if (fullHeightHeader < 0 || cursorY != fullHeightNotes + 19 ||
                    !fullHeightCard[cursorY].Contains("line 24") ||
                    !fullHeightCard[fullHeightNotes].Contains("earlier note text") ||
                    !fullHeightCard[fullHeightNotes + 20].StartsWith("+"))
                    throw new InvalidDataException("Long multiline note caret-at-end scrolling self-test failed.");
                NoteBuffers[2] = previousSelectedNote;
                caret = previousCaretForMultiline;
                string[] resized = new string[10];
                for (int row = 0; row < resized.Length; row++) resized[row] = "";
                selectedCardRows.Clear();
                selectedCardBorderRows.Clear();
                BuildCallRows(resized, 40, resized.Length);
                if (!resized[1].StartsWith("+") || !resized[2].Contains("Call #2") ||
                    !resized[2].StartsWith("|") || !resized[3].StartsWith("+") ||
                    !resized[8].Contains("Ctrl+Z"))
                    throw new InvalidDataException("Compact resized rendering self-test failed.");
                nextCallId = 4;
                Undo[2] = new List<EditAction> { new EditAction { Field = "Notes", Position = 0, Removed = "", Inserted = "x" } };
                Redo[2] = new List<EditAction> { new EditAction { Field = "Notes", Position = 0, Removed = "", Inserted = "x" } };
                RequestDeleteCall();
                HandleDeleteConfirmationKey(ConsoleKey.N);
                if (calls.Count != 3 || deleteConfirmation)
                    throw new InvalidDataException("Call deletion cancellation self-test failed.");
                RequestDeleteCall();
                HandleDeleteConfirmationKey(ConsoleKey.Y);
                if (calls.Count != 2 || selected != 1 || calls[selected].Id != 3 ||
                    NoteBuffers.ContainsKey(2) || Undo.ContainsKey(2) || Redo.ContainsKey(2) || nextCallId != 4)
                    throw new InvalidDataException("Confirmed call deletion state self-test failed.");
                List<CallSnapshot> afterDelete = new List<CallSnapshot>();
                foreach (CallRecord remaining in calls)
                {
                    NoteBuffer remainingNote = NoteBuffers[remaining.Id];
                    afterDelete.Add(new CallSnapshot {
                        Id = remaining.Id, StartTime = remaining.StartTime, EndTime = remaining.EndTime,
                        CallerName = remaining.CallerName, Location = remaining.Location,
                        Number = remaining.Number, Inc = remaining.Inc,
                        Type = remaining.Type, Status = remaining.Status, Notes = "", Note = remainingNote.GetSnapshot()
                    });
                }
                CallWriter.Write(path, afterDelete.ToArray());
                List<CallRecord> afterDeleteRead = CallHistory.Read(path);
                if (afterDeleteRead.Count != 2 || afterDeleteRead[0].Id != 1 || afterDeleteRead[1].Id != 3)
                    throw new InvalidDataException("Deleted call was written back to the history self-test.");
            }
            finally { calls = oldCalls; selected = oldSelected; caret = oldCaret; historyTop = oldHistoryTop; }
            Console.WriteLine("Round-trip passed: edited {0:N0}-character note, escaped strings and UTF-8; {1:N0} ms.",
                expected.Length, timer.ElapsedMilliseconds);
            Console.WriteLine("Piece snapshot immutability, inline note editing, bordered cards, and compact resize layout passed.");
            Console.WriteLine("Markdown keyword, number, punctuation, bracket, and marker highlighting passed.");
            Console.WriteLine("Call search across fields and notes, newest-first results, and jump-to-call passed.");
            Console.WriteLine("Clean whole-call clipboard and notes-only multiline export formatting passed.");
            Console.WriteLine("Call deletion confirmation, cancellation, state cleanup, and saved-history removal passed.");
            return 0;
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
