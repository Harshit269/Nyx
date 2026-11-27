using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PhoneBridge;

/// <summary>Checks for values that arrive from the phone or the network before they are used.</summary>
public static class InputCheck
{
    /// <summary>
    /// A number you can call: digits (3 or more) with the usual + ( ) - and spaces, no letters.
    /// Texts from "AD-SBIUPI-S" and other business senders can't be called back.
    /// </summary>
    public static bool IsDialable(string s) =>
        !string.IsNullOrWhiteSpace(s) && s.Count(char.IsDigit) >= 3 && s.All(c => char.IsDigit(c) || "+()- .".Contains(c));

    /// <summary>A dotted IPv4 address and nothing else (no ports, spaces, or shorthand like "1").</summary>
    public static bool IsIPv4(string s) =>
        s.Length is >= 7 and <= 15 &&
        Regex.IsMatch(s, @"^\d{1,3}(\.\d{1,3}){3}$") &&
        IPAddress.TryParse(s, out var a) && a.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>A colour like #1A2B3C.</summary>
    public static bool IsHexColour(string s) => Regex.IsMatch(s, "^#[0-9A-Fa-f]{6}$");
}
