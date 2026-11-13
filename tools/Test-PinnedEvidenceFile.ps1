# Windows-only tests for handle-relative preflight evidence creation.
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not [OperatingSystem]::IsWindows()) {
    throw "Pinned evidence writer tests require Windows."
}

Add-Type -Path (Join-Path $PSScriptRoot "PinnedEvidenceFile.cs") -ErrorAction Stop
$root = Join-Path ([IO.Path]::GetTempPath()) ("DiskRescue-PinnedEvidence-" + [guid]::NewGuid().ToString("N"))
$directory = Join-Path $root "evidence"
[IO.Directory]::CreateDirectory($directory) | Out-Null

try {
    $leaf = "preflight-evidence.json"
    $expected = [Text.UTF8Encoding]::new($false).GetBytes('{"schema":"test"}' + [Environment]::NewLine)

    $stream = [DiskRescue.Preflight.PinnedEvidenceFile]::CreateNew($directory, $leaf)
    try {
        $stream.Write($expected, 0, $expected.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }

    $target = Join-Path $directory $leaf
    $actual = [IO.File]::ReadAllBytes($target)
    if ([Convert]::ToHexString($actual) -ne [Convert]::ToHexString($expected)) {
        throw "Pinned evidence writer did not preserve the expected bytes."
    }

    $collisionRejected = $false
    try {
        $collision = [DiskRescue.Preflight.PinnedEvidenceFile]::CreateNew($directory, $leaf)
        $collision.Dispose()
    }
    catch { $collisionRejected = $true }
    if (-not $collisionRejected) { throw "Pinned evidence writer unexpectedly replaced an existing target." }

    $afterCollision = [IO.File]::ReadAllBytes($target)
    if ([Convert]::ToHexString($afterCollision) -ne [Convert]::ToHexString($expected)) {
        throw "Existing evidence bytes changed after a competing create attempt."
    }

    $invalidLeafRejected = $false
    try {
        $invalid = [DiskRescue.Preflight.PinnedEvidenceFile]::CreateNew($directory, "..\\escape.json")
        $invalid.Dispose()
    }
    catch { $invalidLeafRejected = $true }
    if (-not $invalidLeafRejected) { throw "Pinned evidence writer accepted a path traversal leaf." }


    # A junction is a reparse-point directory. The native helper must reject
    # the component itself rather than traversing into its target.
    $junctionTarget = Join-Path $root "junction-target"
    $junctionPath = Join-Path $root "junction"
    [IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
    New-Item -ItemType Junction -Path $junctionPath -Target $junctionTarget -ErrorAction Stop | Out-Null
    $reparseRejected = $false
    try {
        $throughJunction = [DiskRescue.Preflight.PinnedEvidenceFile]::CreateNew($junctionPath, "must-not-exist.json")
        $throughJunction.Dispose()
    }
    catch [IO.IOException] { $reparseRejected = $true }
    if (-not $reparseRejected) { throw "Pinned evidence writer followed a reparse-point directory." }
    if (Test-Path -LiteralPath (Join-Path $junctionTarget "must-not-exist.json")) {
        throw "Pinned evidence writer created a file through a reparse-point directory."
    }

    # Deterministically attempt to replace the directory after the helper has
    # pinned and validated it, but before it creates the leaf relative to that handle.
    $raceDirectory = Join-Path $root "race-original"
    $movedDirectory = Join-Path $root "race-original-moved"
    [IO.Directory]::CreateDirectory($raceDirectory) | Out-Null
    $moveState = @{ Moved = $false; Blocked = $false }
    $afterPinned = [Action] {
        try {
            [IO.Directory]::Move($raceDirectory, $movedDirectory)
            [IO.Directory]::CreateDirectory($raceDirectory) | Out-Null
            $moveState.Moved = $true
        }
        catch [Exception] {
            $moveState.Blocked = $true
        }
    }
    $testSeam = [DiskRescue.Preflight.PinnedEvidenceFile].GetMethod(
        "CreateNewForTest",
        [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static)
    if ($null -eq $testSeam) { throw "Pinned-directory race test seam was not found." }
    $invokeArguments = [object[]]::new(3)
    $invokeArguments[0] = [string]$raceDirectory
    $invokeArguments[1] = [string]"race-evidence.json"
    $invokeArguments[2] = [Action]$afterPinned
    $raceStream = $testSeam.Invoke($null, $invokeArguments)
    try {
        $raceBytes = [Text.Encoding]::UTF8.GetBytes("pinned-directory-winner")
        $raceStream.Write($raceBytes, 0, $raceBytes.Length)
        $raceStream.Flush($true)
    }
    finally { $raceStream.Dispose() }

    if ($moveState.Moved) {
        $pinnedTarget = Join-Path $movedDirectory "race-evidence.json"
        $substituteTarget = Join-Path $raceDirectory "race-evidence.json"
        if (-not (Test-Path -LiteralPath $pinnedTarget) -or (Test-Path -LiteralPath $substituteTarget)) {
            throw "Evidence creation followed the substituted directory instead of the pinned directory handle."
        }
    }
    elseif ($moveState.Blocked) {
        if (-not (Test-Path -LiteralPath (Join-Path $raceDirectory "race-evidence.json"))) {
            throw "Directory replacement was blocked, but evidence was not created in the original directory."
        }
    }
    else {
        throw "Directory replacement race callback did not record an outcome."
    }

    # Inject write and flush failures through the same disposal contract used in production.
    $failureTestType = @'
using System;
using System.IO;

public sealed class FaultInjectingEvidenceStream : MemoryStream
{
    private readonly bool failWrite;
    private readonly bool failFlush;
    public bool WasDisposed { get; private set; }

    public FaultInjectingEvidenceStream(bool failWrite, bool failFlush)
    {
        this.failWrite = failWrite;
        this.failFlush = failFlush;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (failWrite) throw new IOException("Injected evidence write failure.");
        base.Write(buffer, offset, count);
    }

    public override void Flush()
    {
        if (failFlush) throw new IOException("Injected evidence flush failure.");
        base.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}

public static class EvidenceWriteFailureTests
{
    public static void Run()
    {
        Type helperType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            helperType = assembly.GetType("DiskRescue.Preflight.PinnedEvidenceFile", false);
            if (helperType != null) break;
        }
        if (helperType == null) throw new InvalidOperationException("Pinned evidence helper type was not loaded.");
        var helper = helperType.GetMethod("WriteAndFlushForTest",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (helper == null) throw new InvalidOperationException("Write/flush failure seam was not found.");
        var bytes = new byte[] { 1, 2, 3 };

        foreach (var failWrite in new[] { true, false })
        {
            var stream = new FaultInjectingEvidenceStream(failWrite, !failWrite);
            var arguments = new object[] {
                stream, bytes,
                new Action<Stream, byte[]>((target, data) => target.Write(data, 0, data.Length)),
                new Action<Stream>(target => target.Flush())
            };
            bool failed = false;
            try { helper.Invoke(null, arguments); }
            catch (System.Reflection.TargetInvocationException ex)
            {
                if (!(ex.InnerException is IOException)) throw;
                failed = true;
            }
            if (!failed) throw new InvalidOperationException("Injected write/flush failure was not propagated.");
            if (!stream.WasDisposed) throw new InvalidOperationException("Evidence stream was not disposed after injected failure.");
        }
    }
}
'@
    if (-not ("EvidenceWriteFailureTests" -as [type])) {
        Add-Type -TypeDefinition $failureTestType -ErrorAction Stop
    }
    [EvidenceWriteFailureTests]::Run()

    $concurrentDirectory = Join-Path $root "concurrent"
    [IO.Directory]::CreateDirectory($concurrentDirectory) | Out-Null
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Reflection;

public static class PinnedEvidenceConcurrencyTest
{
    public static int Run(string directory)
    {
        var helperType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("DiskRescue.Preflight.PinnedEvidenceFile", false))
            .FirstOrDefault(t => t != null);
        if (helperType == null) throw new InvalidOperationException("Pinned evidence helper type was not loaded.");
        var create = helperType.GetMethod("CreateNew", BindingFlags.Public | BindingFlags.Static);
        if (create == null) throw new InvalidOperationException("Pinned evidence CreateNew method was not found.");
        int created = 0;
        int rejected = 0;
        Parallel.For(0, 16, i =>
        {
            try
            {
                using (var stream = (FileStream)create.Invoke(null, new object[] { directory, "single-winner.json" }))
                {
                    var bytes = Encoding.UTF8.GetBytes("winner");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                Interlocked.Increment(ref created);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException)
            {
                Interlocked.Increment(ref rejected);
            }
        });
        if (created != 1 || rejected != 15)
            throw new InvalidOperationException("Concurrent create did not produce exactly one winner.");
        var content = File.ReadAllText(Path.Combine(directory, "single-winner.json"));
        if (content != "winner")
            throw new InvalidOperationException("Concurrent create winner content was corrupted.");
        return created;
    }
}
'@ -ErrorAction Stop
    $winnerCount = [PinnedEvidenceConcurrencyTest]::Run($concurrentDirectory)
    if ($winnerCount -ne 1) { throw "Concurrent create test did not produce one winner." }

    Write-Output "Pinned evidence writer: create-new, durable flush, collision preservation, leaf validation, reparse-point rejection, controlled directory-replacement race, injected write/flush failure disposal, and concurrent single-winner creation passed."
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
