namespace KoTrans.App;

// Pure chord state, shared by the real low-level hook and Background tests.
internal sealed class PassThroughHotkey
{
    private uint modifiers, key;
    private bool down;
    internal uint Key => key;
    internal void Configure(uint modifiers, uint key) { this.modifiers = modifiers; this.key = key; down = false; }
    internal void Reset() { key = 0; down = false; }
    internal bool Observe(uint observedKey, uint observedModifiers, bool pressed, bool injected)
    {
        if (key == 0 || observedKey != key || injected) return false;
        if (!pressed) { down = false; return false; }
        bool first = !down; down = true;
        return first && observedModifiers == modifiers;
    }
}
