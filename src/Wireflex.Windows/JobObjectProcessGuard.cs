using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Wireflex.Core.Processes;

namespace Wireflex.Windows;

/// <summary>
/// Amarra os processos filhos (scrcpy e os adb que ele abre) a um Job Object com
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>. Quando o Wireflex termina, até à força (<c>taskkill /F</c>,
/// crash, Stop do depurador), o Windows fecha o handle do job e mata todos eles.
/// </summary>
/// <remarks>
/// Só os processos passados a <see cref="Track"/> entram no job; o Wireflex não. Assim o servidor do adb,
/// que é compartilhado e sobe por conta própria, continua rodando depois que o Wireflex sai.
/// </remarks>
public sealed partial class JobObjectProcessGuard : IChildProcessGuard, IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly SafeJobHandle _job;

    public JobObjectProcessGuard()
    {
        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Não foi possível criar o Job Object.");
        }

        var info = new JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationData>()))
        {
            var error = Marshal.GetLastPInvokeError();
            _job.Dispose();
            throw new Win32Exception(error, "Não foi possível configurar o Job Object.");
        }
    }

    public void Track(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(_job.IsClosed, this);

        if (!AssignProcessToJobObject(_job, process.SafeHandle))
        {
            // Processo já terminou: nada a amarrar.
            if (process.HasExited)
            {
                return;
            }

            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Não foi possível amarrar o processo ao Wireflex.");
        }
    }

    /// <summary>Fecha o job: mata na hora os processos amarrados que ainda estiverem rodando.</summary>
    public void Dispose() => _job.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeJobHandle job,
        int infoClass,
        ref JobObjectExtendedLimitInformationData info,
        uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationData
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
