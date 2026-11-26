using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VPNDesktop;

internal sealed class WindowsKillSwitch : IDisposable
{
    private const uint RpcAuthWinnt = 10;
    private const uint DynamicSession = 1;
    private const uint Permit = 0x1002;
    private const uint Block = 0x1001;
    private const uint Equal = 0;
    private const uint FlagsAllSet = 6;
    private const uint Loopback = 1;
    private const uint UInt8 = 1;
    private const uint UInt32 = 3;
    private const uint UInt64 = 4;
    private const uint ByteBlob = 12;
    private static readonly Guid ApplicationId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid Flags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private static readonly Guid LocalInterface = new("4cd62a49-59c3-4969-b7f3-bda5d32890a4");
    private static readonly Guid ArrivalInterface = new("618a9b6d-386b-4136-ad6e-b51587cfb1cd");
    private static readonly (Guid Key, bool Inbound)[] Layers =
    [
        (new("c38d57d1-05a7-4c33-904f-7fbceee60e82"), false),
        (new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"), false),
        (new("e1cd9fe7-f4b5-4273-96c0-592e487b8650"), true),
        (new("a3b42c97-9f04-4672-b87e-cee9c483257f"), true)
    ];

    private readonly EngineHandle engine;
    private readonly Guid sublayer = Guid.NewGuid();
    private ulong[] adapterFilters = [];
    private ulong? adapterLuid;

    private WindowsKillSwitch(EngineHandle engine) => this.engine = engine;

    public static WindowsKillSwitch Arm(string corePath)
    {
        var session = new Session { Flags = DynamicSession, TransactionTimeout = 3000 };
        Check(FwpmEngineOpen0(IntPtr.Zero, RpcAuthWinnt, IntPtr.Zero, ref session, out var engine), "открыть Windows Filtering Platform");
        var guard = new WindowsKillSwitch(engine);
        try
        {
            guard.Install(corePath);
            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    public void AllowTunAdapter()
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(item =>
            item.Name.Equals(RoutingConfig.TunDevice, StringComparison.OrdinalIgnoreCase) &&
            item.OperationalStatus == OperationalStatus.Up);
        if (!Guid.TryParse(adapter?.Id, out var id))
            throw new IOException("Kill Switch не нашёл активный адаптер VPNDesktop.");
        Check(ConvertInterfaceGuidToLuid(ref id, out ulong luid), "определить интерфейс TUN");
        SetAdapter(luid);
    }

    public void BlockTunAdapter() => SetAdapter(null);

    private void Install(string corePath)
    {
        Check(FwpmGetAppIdFromFileName0(corePath, out IntPtr appId), "определить приложение Mihomo");
        IntPtr name = IntPtr.Zero;
        try
        {
            name = Marshal.StringToHGlobalUni("VPN Desktop Kill Switch");
            Transaction(() =>
            {
                var layer = new Sublayer { Key = sublayer, Weight = ushort.MaxValue, Display = new() { Name = name } };
                Check(FwpmSubLayerAdd0(engine, ref layer, IntPtr.Zero), "создать правила Kill Switch");
                foreach (var (key, _) in Layers)
                {
                    AddFilter(key, Permit, 15, new(Flags, FlagsAllSet, new(UInt32, (IntPtr)Loopback)));
                    AddFilter(key, Permit, 15, new(ApplicationId, Equal, new(ByteBlob, appId)));
                    AddFilter(key, Block, 0);
                }
            });
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            FwpmFreeMemory0(ref appId);
        }
    }

    private void SetAdapter(ulong? luid)
    {
        if (adapterLuid == luid)
            return;
        IntPtr value = Marshal.AllocHGlobal(sizeof(ulong));
        try
        {
            ulong[] next = [];
            Transaction(() =>
            {
                foreach (ulong filter in adapterFilters)
                    Check(FwpmFilterDeleteById0(engine, filter), "убрать разрешение для прежнего адаптера");
                if (luid is { } allowed)
                {
                    Marshal.WriteInt64(value, unchecked((long)allowed));
                    next = Layers.Select(layer => AddFilter(layer.Key, Permit, 15, new(
                        layer.Inbound ? ArrivalInterface : LocalInterface, Equal, new(UInt64, value)))).ToArray();
                }
            });
            adapterFilters = next;
            adapterLuid = luid;
        }
        finally { Marshal.FreeHGlobal(value); }
    }

    private ulong AddFilter(Guid layer, uint action, byte weight, Condition? condition = null)
    {
        IntPtr memory = IntPtr.Zero;
        IntPtr name = Marshal.StringToHGlobalUni("VPN Desktop Kill Switch");
        try
        {
            if (condition is { } match)
            {
                memory = Marshal.AllocHGlobal(Marshal.SizeOf<Condition>());
                Marshal.StructureToPtr(match, memory, false);
            }
            var filter = new Filter
            {
                Display = new() { Name = name },
                Layer = layer,
                Sublayer = sublayer,
                Weight = new(UInt8, (IntPtr)weight),
                ConditionCount = condition is null ? 0u : 1u,
                Conditions = memory,
                Action = new() { Type = action }
            };
            Check(FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out ulong id), "добавить фильтр IPv4 или IPv6");
            return id;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
            Marshal.FreeHGlobal(name);
        }
    }

    private void Transaction(Action apply)
    {
        Check(FwpmTransactionBegin0(engine, 0), "начать изменение фильтров");
        try
        {
            apply();
            Check(FwpmTransactionCommit0(engine), "применить фильтры");
        }
        catch
        {
            FwpmTransactionAbort0(engine);
            throw;
        }
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0)
            throw new IOException($"Kill Switch: не удалось {operation} (0x{result:X8}).");
    }

    public void Dispose() => engine.Dispose();

    private sealed class EngineHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public EngineHandle() : base(true) { }
        protected override bool ReleaseHandle() => FwpmEngineClose0(handle) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayData
    {
        public IntPtr Name, Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Session
    {
        public Guid Key;
        public DisplayData Display;
        public uint Flags, TransactionTimeout, ProcessId;
        public IntPtr Sid, Username;
        public int KernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Sublayer
    {
        public Guid Key;
        public DisplayData Display;
        public uint Flags;
        public IntPtr ProviderKey;
        public Blob ProviderData;
        public ushort Weight;
    }

    // The FWP value union stores either a small integer or a native pointer.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Value(uint type, IntPtr data)
    {
        public readonly uint Type = type;
        public readonly IntPtr Data = data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Condition(Guid key, uint match, Value value)
    {
        public readonly Guid Key = key;
        public readonly uint Match = match;
        public readonly Value Value = value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterAction
    {
        public uint Type;
        public Guid FilterType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Filter
    {
        public Guid Key;
        public DisplayData Display;
        public uint Flags;
        public IntPtr ProviderKey;
        public Blob ProviderData;
        public Guid Layer, Sublayer;
        public Value Weight;
        public uint ConditionCount;
        public IntPtr Conditions;
        public FilterAction Action;
        public ulong ContextLow, ContextHigh;
        public IntPtr Reserved;
        public ulong Id;
        public Value EffectiveWeight;
    }

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmEngineOpen0(IntPtr server, uint auth, IntPtr identity, ref Session session, out EngineHandle engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmTransactionBegin0(EngineHandle engine, uint flags);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmTransactionCommit0(EngineHandle engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmTransactionAbort0(EngineHandle engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint FwpmGetAppIdFromFileName0(string path, out IntPtr id);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern void FwpmFreeMemory0(ref IntPtr memory);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmSubLayerAdd0(EngineHandle engine, ref Sublayer layer, IntPtr securityDescriptor);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmFilterAdd0(EngineHandle engine, ref Filter filter, IntPtr securityDescriptor, out ulong id);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    private static extern uint FwpmFilterDeleteById0(EngineHandle engine, ulong id);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint ConvertInterfaceGuidToLuid(ref Guid guid, out ulong luid);
}

