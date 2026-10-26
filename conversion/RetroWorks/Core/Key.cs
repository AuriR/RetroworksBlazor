namespace RetroWorks.Core;

/// <summary>
/// Key tokens used throughout the app. The JS keydown handler maps browser events to these
/// strings, which are then consumed by the async Key() loop in App/Doc.
/// </summary>
public static class Key
{
    public const string Up = "UP";
    public const string Down = "DOWN";
    public const string Left = "LEFT";
    public const string Right = "RIGHT";
    public const string Home = "HOME";
    public const string End = "END";
    public const string PgUp = "PGUP";
    public const string PgDn = "PGDN";
    public const string Backspace = "BKSP";
    public const string Delete = "DEL";
    public const string Enter = "ENTER";
    public const string Escape = "ESC";
    public const string Tab = "TAB";
    public const string BTab = "BTAB";
    public const string F1 = "F1";

    /// <summary>Alt+letter produces tokens like "A-Q" (A for Alt, Q for the letter).</summary>
    public static string Alt(string letter) => "A-" + letter;

    /// <summary>Check if a token is an Alt+letter command.</summary>
    public static bool IsAlt(string k) => k.Length == 3 && k[0] == 'A' && k[1] == '-';

    /// <summary>The letter part of an Alt token ("Q" from "A-Q").</summary>
    public static char AltLetter(string k) => k[2];
}
