using System.Collections.Generic;
using System.Runtime.InteropServices;
using KoTrans.Infrastructure;

namespace KoTrans.App;

// Shared decoding for registration and live triggers. Joy1..4 remain compatible.
internal static class GamepadInput
{
    internal static readonly (string Id, string Label, uint Mask)[] Buttons = [
        ("Joy1", "A", 0x1000), ("Joy2", "B", 0x2000), ("Joy3", "X", 0x4000), ("Joy4", "Y", 0x8000),
        ("LeftShoulder", "LB", 0x100), ("RightShoulder", "RB", 0x200),
        ("LeftTrigger", "LT", 0x10000), ("RightTrigger", "RT", 0x20000),
        ("DPadUp", "방향 ↑", 1), ("DPadDown", "방향 ↓", 2), ("DPadLeft", "방향 ←", 4), ("DPadRight", "방향 →", 8),
        ("LeftThumb", "왼쪽 스틱 누르기", 0x40), ("RightThumb", "오른쪽 스틱 누르기", 0x80),
        ("Start", "시작 / Menu", 0x10), ("Back", "뒤로 / View", 0x20)
    ];
    internal static string Label(string id) => Buttons.FirstOrDefault(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Label ?? id;
    internal static uint Parse(string value)
    {
        uint result = 0;
        foreach (string id in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
            var button = Buttons.FirstOrDefault(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (button.Mask == 0) throw new TranslationException("지원하지 않는 게임패드 버튼입니다. 해당 항목을 삭제하고 버튼을 직접 눌러 다시 등록하세요.");
            result |= button.Mask;
        }
        return result;
    }
    internal static uint Decode(ushort buttons, byte leftTrigger, byte rightTrigger) =>
        (uint)(buttons & 0xF3FF) | (leftTrigger > 30 ? 0x10000u : 0) | (rightTrigger > 30 ? 0x20000u : 0);
    internal static uint Rising(uint previous, uint current) => current & ~previous;
    internal static uint? Read(uint index) => XInputGetState(index, out var state) == 0
        ? Decode(state.Gamepad.Buttons, state.Gamepad.LeftTrigger, state.Gamepad.RightTrigger) : null;
    [StructLayout(LayoutKind.Sequential)] private struct Pad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint Packet; public Pad Gamepad; }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out State state);
}
