using System;

namespace HitboxClone;

/// <summary>
/// Printing for the self-test suites, flushed on every line.
///
/// <c>GD.Print</c> goes through the engine's own printing, and when stdout is a file rather than a
/// console the C runtime block-buffers it. A suite redirected to a log therefore shows nothing at
/// all until the process exits — which makes a run that is merely slow indistinguishable from one
/// that has hung, and makes it impossible to see *where* a hang happened. Three separate runs were
/// abandoned on that ambiguity before this existed.
///
/// <see cref="Console"/> rather than <c>GD.Print</c>, flushed on every line. Both end up on the
/// same stdout, so printing through both just doubled every line.
/// </summary>
public static class TestLog
{
    public static void Line(string s)
    {
        Console.Out.WriteLine(s);
        Console.Out.Flush();
    }

    public static void Fail(string s)
    {
        Console.Error.WriteLine(s);
        Console.Error.Flush();
    }
}
