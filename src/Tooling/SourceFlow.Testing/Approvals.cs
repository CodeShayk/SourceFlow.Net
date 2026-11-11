using System.Runtime.CompilerServices;
using System.Text;

namespace SourceFlow.Testing;

/// <summary>
/// Approved-file snapshots: compares a received text with <c>Approved/&lt;name&gt;.approved.txt</c> next to the
/// calling test file. On a mismatch the received text is written to <c>&lt;name&gt;.received.txt</c> and
/// <see cref="ApprovalMismatchException"/> is thrown. Set the environment variable
/// <c>SOURCEFLOW_APPROVE=1</c> to accept every received file (review the diff before committing).
/// </summary>
public static class Approvals
{
    /// <summary>The environment variable that turns mismatches into approvals.</summary>
    public const string ApproveVariable = "SOURCEFLOW_APPROVE";

    /// <summary>Asserts that <paramref name="received"/> equals the approved snapshot called <paramref name="name"/>.</summary>
    /// <param name="received">The text produced by the code under test.</param>
    /// <param name="name">A file-name-safe snapshot name, unique within the test file's folder.</param>
    /// <param name="callerFilePath">Supplied by the compiler; locates the <c>Approved</c> folder.</param>
    public static void AssertMatches(string received, string name, [CallerFilePath] string callerFilePath = "")
    {
        ArgumentNullException.ThrowIfNull(received);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"Snapshot name '{name}' contains characters that are not valid in a file name.", nameof(name));
        }

        string directory = Path.Combine(Path.GetDirectoryName(callerFilePath) ?? ".", "Approved");
        string approvedPath = Path.Combine(directory, name + ".approved.txt");
        string receivedPath = Path.Combine(directory, name + ".received.txt");
        string normalized = Normalize(received);

        string? approved = File.Exists(approvedPath) ? Normalize(File.ReadAllText(approvedPath)) : null;
        if (approved == normalized)
        {
            if (File.Exists(receivedPath))
            {
                File.Delete(receivedPath);
            }

            return;
        }

        Directory.CreateDirectory(directory);
        if (string.Equals(Environment.GetEnvironmentVariable(ApproveVariable), "1", StringComparison.Ordinal))
        {
            File.WriteAllText(approvedPath, normalized, Utf8NoBom);
            if (File.Exists(receivedPath))
            {
                File.Delete(receivedPath);
            }

            return;
        }

        File.WriteAllText(receivedPath, normalized, Utf8NoBom);
        throw new ApprovalMismatchException(approvedPath, receivedPath, approved is null ? null : FirstDifference(approved, normalized));
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string Normalize(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
    }

    private static string FirstDifference(string approved, string received)
    {
        string[] a = approved.Split('\n');
        string[] r = received.Split('\n');
        int lines = Math.Max(a.Length, r.Length);
        for (int i = 0; i < lines; i++)
        {
            string left = i < a.Length ? a[i] : "<end of file>";
            string right = i < r.Length ? r[i] : "<end of file>";
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                return $"line {i + 1}:\n  approved: {left}\n  received: {right}";
            }
        }

        return "no line difference (whitespace only)";
    }
}

/// <summary>Thrown when a received snapshot differs from its approved file.</summary>
public sealed class ApprovalMismatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ApprovalMismatchException(string approvedPath, string receivedPath, string? difference)
        : base(BuildMessage(approvedPath, receivedPath, difference))
    {
        ApprovedPath = approvedPath;
        ReceivedPath = receivedPath;
    }

    /// <summary>The approved file (may not exist yet).</summary>
    public string ApprovedPath { get; }

    /// <summary>The received file written for review.</summary>
    public string ReceivedPath { get; }

    private static string BuildMessage(string approvedPath, string receivedPath, string? difference)
    {
        string first = difference is null ? "No approved file exists yet." : "First difference at " + difference;
        return $"Snapshot does not match '{approvedPath}'. {first}\nReview '{receivedPath}', then rename it to *.approved.txt " +
               $"or rerun with {Approvals.ApproveVariable}=1.";
    }
}
