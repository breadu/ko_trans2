using System.Runtime.InteropServices;

namespace KoTrans.Ocr;

internal sealed record DirectMlAdapter(int Index, string Name, string Id, ulong DedicatedBytes);

// DML's device_id is the original EnumAdapters index, NOT the index in our
// filtered/preference-sorted list. Probe DX12; exclude software/remote adapters.
internal static class DirectMlDevice
{
    internal static DirectMlAdapter Selected { get; private set; } = null!;
    internal static void Select()
    {
        Guid factoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref factoryId, out nint factory));
        try {
            var list = new List<DirectMlAdapter>();
            for (uint i = 0; i < 32; i++) {
                int hr = Method<EnumAdapters>(factory, 7)(factory, i, out nint adapter);
                if (hr == unchecked((int)0x887a0002)) break;
                Marshal.ThrowExceptionForHR(hr);
                try {
                    Marshal.ThrowExceptionForHR(Method<GetDesc>(adapter, 8)(adapter, out var desc));
                    Guid adapter1 = new("29038f61-3839-4626-91fd-086879011a05");
                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(adapter, in adapter1, out nint one));
                    uint flags;
                    try { Marshal.ThrowExceptionForHR(Method<GetDesc1>(one, 10)(one, out var d1)); flags = d1.Flags; }
                    finally { Marshal.Release(one); }
                    if ((flags & 3) != 0) continue; // DXGI_ADAPTER_FLAG_REMOTE | SOFTWARE
                    Guid device = new("189819f1-1db6-4b57-be54-1821339b85f7");
                    if (D3D12CreateDevice(adapter, 0xb000, ref device, nint.Zero) < 0) continue;
                    string id = $"dxgi:{i};luid:{desc.LuidHigh:X8}{desc.LuidLow:X8};vendor:{desc.VendorId:X4};device:{desc.DeviceId:X4}";
                    list.Add(new((int)i, desc.Description.TrimEnd('\0'), id, desc.DedicatedVideoMemory));
                } finally { Marshal.Release(adapter); }
            }
            Selected = list.OrderByDescending(a => a.DedicatedBytes).ThenBy(a => a.Index).FirstOrDefault()
                ?? throw new NotSupportedException("no-dx12-hardware-adapter");
        } finally { Marshal.Release(factory); }
    }
    private static T Method<T>(nint instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters(nint self, uint index, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDesc(nint self, out AdapterDesc description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDesc1(nint self, out AdapterDesc1 description);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubsystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1 { public AdapterDesc Description; public uint Flags; }
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid id, out nint factory);
    [DllImport("d3d12.dll")] private static extern int D3D12CreateDevice(nint adapter, uint featureLevel, ref Guid id, nint device);
}
