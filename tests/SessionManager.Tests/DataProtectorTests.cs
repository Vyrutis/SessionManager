using System.IO;
using System.Text;
using SessionManager;

/// <summary>Encrypting the saved files: Windows account key, master password, and moving old plain files over.</summary>
static class DataProtectorTests
{
    public static void Run(Action<string, bool, string> check)
    {
        DataProtector.Enabled = true;
        try
        {
            var plain = Encoding.UTF8.GetBytes("{\"Host\":\"10.0.0.5\"}");

            DataProtector.Reset();
            if (OperatingSystem.IsWindows())
            {
                var sealedWin = DataProtector.Protect(plain);
                check("protect: Windows mode hides the text", DataProtector.IsProtected(sealedWin) && !Encoding.UTF8.GetString(sealedWin).Contains("10.0.0.5"), "");
                check("protect: Windows mode reads back", DataProtector.Unprotect(sealedWin).AsSpan().SequenceEqual(plain), "");
                var edited = (byte[])sealedWin.Clone();
                edited[^1] ^= 1;
                check("protect: an edited Windows file is refused", Throws<InvalidDataException>(() => DataProtector.Unprotect(edited)), "");
            }

            DataProtector.SetPassword("correct horse");
            var sealedPw = DataProtector.Protect(plain);
            check("protect: password mode hides the text", DataProtector.ModeOf(sealedPw) == ProtectionMode.Password && !Encoding.UTF8.GetString(sealedPw).Contains("10.0.0.5"), "");
            check("protect: password mode reads back", DataProtector.Unprotect(sealedPw, allowPrompt: false).AsSpan().SequenceEqual(plain), "");
            check("protect: two saves differ (fresh nonce)", !DataProtector.Protect(plain).AsSpan().SequenceEqual(sealedPw), "");

            var tampered = (byte[])sealedPw.Clone();
            tampered[^1] ^= 1;
            DataProtector.Reset();
            var asked = new List<bool>();
            DataProtector.AskPassword = wrong => { asked.Add(wrong); return asked.Count == 1 ? "wrong" : "correct horse"; };
            check("protect: a wrong password is asked again and then works", DataProtector.Unprotect(sealedPw).AsSpan().SequenceEqual(plain) && asked.SequenceEqual(new[] { false, true }), string.Join(",", asked));
            DataProtector.Reset();
            DataProtector.AskPassword = _ => null;
            check("protect: cancelling the prompt cancels the load", Throws<OperationCanceledException>(() => DataProtector.Unprotect(sealedPw)), "");
            DataProtector.Reset();
            DataProtector.AskPassword = _ => "correct horse";
            DataProtector.Unprotect(sealedPw);
            check("protect: an edited password file never opens", Throws<OperationCanceledException>(() =>
            {
                DataProtector.Reset();
                int n = 0;
                DataProtector.AskPassword = _ => n++ < 2 ? "correct horse" : null;
                DataProtector.Unprotect(tampered);
            }), "");
            DataProtector.AskPassword = null;
            DataProtector.Reset();
            check("protect: without a prompt a password file stays locked", Throws<InvalidDataException>(() => DataProtector.Unprotect(sealedPw, allowPrompt: false)), "");
            check("protect: plain data passes through", DataProtector.Unprotect(plain).AsSpan().SequenceEqual(plain) && !DataProtector.IsProtected(plain), "");

            // Whole store: old plain file is protected on load, and a password survives a restart.
            var dir = Path.Combine(Path.GetTempPath(), "sm-protect-test-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                var file = Path.Combine(dir, "x.json");
                File.WriteAllText(file, "{\"a\":1}");
                DataProtector.Reset();
                if (OperatingSystem.IsWindows())
                {
                    check("protect: a plain file is rewritten encrypted", DataProtector.ProtectFile(file) && DataProtector.IsProtected(File.ReadAllBytes(file)), "");
                    check("protect: and reads back", DataProtector.ReadAll(file) == "{\"a\":1}", "");
                    check("protect: an already protected file is left alone", !DataProtector.ProtectFile(file), "");
                }
                DataProtector.SetPassword("pw1");
                DataProtector.WriteAll(file, "{\"b\":2}");
                check("protect: password file needs the password after a restart", DataProtector.NeedsPassword(file), "");
                DataProtector.Reset();
                check("protect: TryReadWithoutPassword gives null while locked", DataProtector.TryReadWithoutPassword(file) == null, "");
                DataProtector.AskPassword = _ => "pw1";
                check("protect: the right password opens it", DataProtector.ReadAll(file) == "{\"b\":2}", "");
                check("protect: afterwards it reads silently", DataProtector.TryReadWithoutPassword(file) == "{\"b\":2}", "");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
        finally
        {
            DataProtector.AskPassword = null;
            DataProtector.Reset();
            DataProtector.Enabled = false;
        }
    }

    private static bool Throws<T>(Action a) where T : Exception
    {
        try { a(); return false; }
        catch (T) { return true; }
    }
}
