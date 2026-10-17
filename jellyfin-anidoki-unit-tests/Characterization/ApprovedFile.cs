using System;
using System.IO;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace jellyfin_anidoki_unit_tests.Characterization;

/// <summary>
/// Compares characterization output with <c>Approved/&lt;name&gt;.approved.txt</c> beside the calling test's source file.
/// On a mismatch it writes <c>&lt;name&gt;.received.txt</c> and fails. Setting <c>ANIDOKI_APPROVE=1</c> accepts the
/// received output instead; do that only for a reviewed, intended behaviour change.
/// </summary>
internal static class ApprovedFile {
    public static void Verify(string name, string received, [CallerFilePath] string callerPath = "") {
        string directory = Path.Combine(Path.GetDirectoryName(callerPath)!, "Approved");
        Directory.CreateDirectory(directory);
        string approvedPath = Path.Combine(directory, name + ".approved.txt");
        string receivedPath = Path.Combine(directory, name + ".received.txt");
        received = received.ReplaceLineEndings("\n");

        if (Environment.GetEnvironmentVariable("ANIDOKI_APPROVE") == "1") {
            File.WriteAllText(approvedPath, received);
            File.Delete(receivedPath);
            return;
        }

        string approved = File.Exists(approvedPath) ? File.ReadAllText(approvedPath).ReplaceLineEndings("\n") : "";
        if (approved == received) {
            File.Delete(receivedPath);
            return;
        }

        File.WriteAllText(receivedPath, received);
        Assert.Fail($"{name} differs from its approved file. Compare {receivedPath} with {approvedPath}. " +
                    "If the change is intended and reviewed, rerun with ANIDOKI_APPROVE=1.");
    }
}
