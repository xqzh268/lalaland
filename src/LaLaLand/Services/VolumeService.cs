using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LaLaLand.Services;

/// <summary>
/// Player-only volume through Windows Core Audio. SMTC has no volume API, so we scale the player's own audio
/// sessions — never the system/device volume. The level is a multiplier on top of the player's in-app volume and
/// its slider in the Windows volume mixer.
/// <para>
/// Sessions are searched on every active playback device (players may not use the default one) and matched by
/// package / process. Volume is applied through <c>ISimpleAudioVolume</c> where available, otherwise through
/// <c>IChannelAudioVolume</c> (some Windows builds refuse the former for other processes' sessions).
/// </para>
/// All COM work runs on thread-pool (MTA) threads.
/// </summary>
public sealed class VolumeService
{
    public readonly record struct VolumeState(float Level, bool Muted);

    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private const int eRender = 0, DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 0x17;

    private readonly object _lock = new();
    private readonly Dictionary<string, float> _unmuteLevels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Current level, or null when the player has no audio session (nothing is changed then).</summary>
    public Task<VolumeState?> GetAsync(string? appId) => Run(appId, target => target.Read());

    public Task<VolumeState?> SetAsync(string? appId, float level) => Run(appId, target =>
    {
        target.SetLevel(Math.Clamp(level, 0f, 1f));
        return target.Read();
    });

    public Task<VolumeState?> StepAsync(string? appId, float delta) => Run(appId, target =>
    {
        var current = target.Read();
        float from = current.Muted ? 0 : current.Level;
        target.SetLevel(Math.Clamp(from + delta, 0f, 1f));
        return target.Read();
    });

    public Task<VolumeState?> ToggleMuteAsync(string? appId) => Run(appId, target =>
    {
        target.SetMute(!target.Read().Muted);
        return target.Read();
    });

    private Task<VolumeState?> Run(string? appId, Func<PlayerTarget, VolumeState> work) => Task.Run(() =>
    {
        if (string.IsNullOrEmpty(appId))
            return (VolumeState?)null;
        try
        {
            var sessions = FindPlayerSessions(appId);
            if (sessions.Count == 0)
                return null;
            lock (_lock)
                return work(new PlayerTarget(this, appId, sessions));
        }
        catch (Exception ex)
        {
            Log.Error("Volume operation failed", ex);
            return null;
        }
    });

    /// <summary>Processes that own the player's audio sessions (e.g. AMPLibraryAgent.exe for Apple Music).</summary>
    public Task<uint[]> FindPlayerProcessIdsAsync(string? appId) => Task.Run(() =>
    {
        if (string.IsNullOrEmpty(appId))
            return Array.Empty<uint>();
        try
        {
            var pids = new List<uint>();
            FindPlayerSessions(appId, pids);
            return pids.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error("Finding player processes failed", ex);
            return Array.Empty<uint>();
        }
    });

    // ---------------------------------------------------------------- session discovery

    /// <summary>All audio sessions of the SMTC source app, across every active playback device.</summary>
    private static List<ISessionVolume> FindPlayerSessions(string appId, List<uint>? pidsOut = null)
    {
        // "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App" -> package name "AppleInc.AppleMusicWin";
        // "Spotify.exe" / "C:\...\cloudmusic.exe" -> process name.
        string packageName = appId.Split('!')[0].Split('_')[0];
        string processName = Path.GetFileNameWithoutExtension(appId.Split('!')[0]);

        var candidates = new List<(uint Pid, string Identifier, IntPtr Session)>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out var devices));
            devices.GetCount(out int deviceCount);
            for (int d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out var device) != 0)
                    continue;
                var iid = IID_IAudioSessionManager2;
                if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var managerObj) != 0)
                    continue;
                if (((IAudioSessionManager2)managerObj).GetSessionEnumerator(out var sessions) != 0)
                    continue;
                sessions.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var raw) != 0 || raw == IntPtr.Zero)
                        continue;
                    var control = (IAudioSessionControl2)Marshal.GetObjectForIUnknown(raw);
                    control.GetSessionIdentifier(out var idPtr);
                    var identifier = Marshal.PtrToStringUni(idPtr) ?? "";
                    Marshal.FreeCoTaskMem(idPtr);
                    control.GetProcessId(out uint pid);
                    candidates.Add((pid, identifier, raw)); // released below
                }
            }

            // Packaged apps: identifier contains the package path. Their helper processes (Apple Music plays
            // through AMPLibraryAgent.exe) may own further sessions without a path, so include same-PID sessions.
            var pids = candidates
                .Where(c => c.Identifier.Contains(packageName, StringComparison.OrdinalIgnoreCase) || ProcessNameIs(c.Pid, processName))
                .Select(c => c.Pid)
                .Where(pid => pid != 0)
                .ToHashSet();
            pidsOut?.AddRange(pids);

            var result = new List<ISessionVolume>();
            foreach (var c in candidates.Where(c => pids.Contains(c.Pid)))
            {
                if (SessionVolume.TryCreate(c.Session) is { } volume)
                    result.Add(volume);
            }
            return result;
        }
        finally
        {
            foreach (var c in candidates)
                Marshal.Release(c.Session);
        }
    }

    private static bool ProcessNameIs(uint pid, string name)
    {
        if (pid == 0)
            return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // exited
        }
    }

    // ---------------------------------------------------------------- volume targets

    private interface ISessionVolume
    {
        float GetLevel();
        void SetLevel(float level);
    }

    private static class SessionVolume
    {
        private static readonly Guid IID_ISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6F7E9E0E62A1");
        private static readonly Guid IID_IChannelAudioVolume = new("1C158861-B533-4B30-B1CF-E853E51C59B8");

        public static ISessionVolume? TryCreate(IntPtr session)
        {
            var iid = IID_ISimpleAudioVolume;
            if (Marshal.QueryInterface(session, ref iid, out var simple) == 0)
            {
                try { return new Simple((ISimpleAudioVolume)Marshal.GetObjectForIUnknown(simple)); }
                finally { Marshal.Release(simple); }
            }
            iid = IID_IChannelAudioVolume;
            if (Marshal.QueryInterface(session, ref iid, out var channel) == 0)
            {
                try { return new Channel((IChannelAudioVolume)Marshal.GetObjectForIUnknown(channel)); }
                finally { Marshal.Release(channel); }
            }
            return null;
        }

        private sealed class Simple(ISimpleAudioVolume volume) : ISessionVolume
        {
            public float GetLevel()
            {
                volume.GetMasterVolume(out float level);
                return level;
            }

            public void SetLevel(float level)
            {
                var ctx = Guid.Empty;
                volume.SetMasterVolume(level, ref ctx);
            }
        }

        /// <summary>Per-channel gain of the session: multiplies with the mixer slider, which stays untouched.</summary>
        private sealed class Channel(IChannelAudioVolume volume) : ISessionVolume
        {
            public float GetLevel()
            {
                volume.GetChannelCount(out uint count);
                if (count == 0)
                    return 1f;
                float max = 0;
                for (uint i = 0; i < count; i++)
                {
                    volume.GetChannelVolume(i, out float level);
                    max = Math.Max(max, level);
                }
                return max;
            }

            public void SetLevel(float level)
            {
                volume.GetChannelCount(out uint count);
                var ctx = Guid.Empty;
                for (uint i = 0; i < count; i++)
                    volume.SetChannelVolume(i, level, ref ctx);
            }
        }
    }

    /// <summary>All sessions of one player, driven together. Mute = level 0 with the previous level remembered.</summary>
    private sealed class PlayerTarget(VolumeService owner, string appId, List<ISessionVolume> sessions)
    {
        public VolumeState Read()
        {
            float level = sessions[0].GetLevel();
            bool muted = level <= 0.0001f && owner._unmuteLevels.ContainsKey(appId);
            return new VolumeState(muted ? owner._unmuteLevels[appId] : level, muted);
        }

        public void SetLevel(float level)
        {
            owner._unmuteLevels.Remove(appId);
            foreach (var s in sessions)
                s.SetLevel(level);
        }

        public void SetMute(bool mute)
        {
            if (mute)
            {
                float current = sessions[0].GetLevel();
                owner._unmuteLevels[appId] = current > 0.0001f ? current : 1f;
                foreach (var s in sessions)
                    s.SetLevel(0);
            }
            else
            {
                float restore = owner._unmuteLevels.TryGetValue(appId, out var l) ? l : 1f;
                owner._unmuteLevels.Remove(appId);
                foreach (var s in sessions)
                    s.SetLevel(restore);
            }
        }
    }

    // ---------------------------------------------------------------- Core Audio COM interop (vtable order matters)

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IntPtr session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        int GetIconPath(out IntPtr path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        int GetGroupingParam(out Guid param);
        int SetGroupingParam(ref Guid param, ref Guid context);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        int GetSessionIdentifier(out IntPtr id);
        int GetSessionInstanceIdentifier(out IntPtr id);
        int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6F7E9E0E62A1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid context);
        int GetMasterVolume(out float level);
    }

    [ComImport, Guid("1C158861-B533-4B30-B1CF-E853E51C59B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IChannelAudioVolume
    {
        int GetChannelCount(out uint count);
        int SetChannelVolume(uint index, float level, ref Guid context);
        int GetChannelVolume(uint index, out float level);
    }
}
