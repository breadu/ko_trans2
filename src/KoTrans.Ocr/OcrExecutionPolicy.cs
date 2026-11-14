using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using KoTrans.Core;
using Microsoft.ML.OnnxRuntime;

namespace KoTrans.Ocr;

// Use one numeric policy across OCR engines and every detector/recognizer session.
// ORT sets its pool workers, but only the first session sets the calling thread.
// Task.Run may use a different CLR worker on the next request, so scope that thread
// explicitly as well. Never carry changed floating-point control beyond OCR work.
internal static class OcrExecutionPolicy
{
    internal static InferenceSession CreateSession(string path, SessionOptions options)
    {
#if OCR_DIRECTML_WORKER
        DirectMlTrace.Prepare(options);
#endif
        var session = new InferenceSession(path, options);
#if OCR_DIRECTML_WORKER
        DirectMlTrace.Register(session);
#endif
        return session;
    }
    internal const uint DenormalMask = 0x03000000; // _MCW_DN, Windows x64 SSE2 control
    internal const uint FlushDenormals = 0x01000000; // _DN_FLUSH: operands and results

    internal static SessionOptions CreateSessionOptions(int threads, bool stopSpinning = false)
    {
        var options = new SessionOptions { IntraOpNumThreads = threads, InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        options.AddSessionConfigEntry("session.set_denormal_as_zero", "1");
        if (stopSpinning) {
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        }
#if OCR_DIRECTML_WORKER
        options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        options.EnableMemoryPattern = false;
        options.AppendExecutionProvider_DML(DirectMlDevice.Selected.Index);
#endif
        return options;
    }

    internal static uint DenormalControl
    {
        get {
            if (Control(out uint current, 0, 0) != 0)
                throw new OcrInitializationException("OCR 부동소수점 실행 상태를 확인할 수 없습니다.");
            return current & DenormalMask;
        }
    }

    // Restore after the catch has completed: CLR/Windows exception dispatch may
    // restore the throw-site FP context after a finally has already restored ours.
    internal static T Run<T>(Func<T> work)
    {
        var scope = Enter();
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        try { result = work(); }
        catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        scope.Dispose();
        failure?.Throw();
        return result;
    }
    internal static void Run(Action work) => Run(() => { work(); return 0; });
    internal static Scope Enter()
    {
        uint previous = DenormalControl;
        if (Control(out _, FlushDenormals, DenormalMask) != 0)
            throw new OcrInitializationException("OCR 부동소수점 실행 정책을 적용할 수 없습니다.");
        return new(previous);
    }

    internal readonly struct Scope(uint previous) : IDisposable
    {
        public void Dispose()
        {
            if (Control(out _, previous, DenormalMask) != 0)
                throw new OcrInitializationException("OCR 부동소수점 실행 상태를 복구할 수 없습니다.");
        }
    }

    [DllImport("ucrtbase.dll", EntryPoint = "_controlfp_s", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Control(out uint current, uint value, uint mask);
}
