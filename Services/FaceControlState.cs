using System.Numerics;

namespace HMSync.Services;

// Dynamic face control (Brio-style) shared state. The UI (Face Control panel + tear-off) writes it; StateCaptureService
// reads it each frame and folds it into the outgoing snapshot so peers drive the gaze via updateLookAt. Self-actor only.
// Per slot (eyes/body/head): an "on" flag + a world-point target. The panel's "set to camera" button fills the point
// from the live camera; the point can also be keyed manually. Static so any component can touch it without new DI wiring.
public static class FaceControlState
{
    public static bool EyesOn;
    public static Vector3 Eyes;
    public static bool BodyOn;
    public static Vector3 Body;
    public static bool HeadOn;
    public static Vector3 Head;
    // "Hold coords" lock: when true, the gaze does NOT auto-clear on movement - it holds its world-point through
    // walking and pivoting, so you can aim at a fixed point (airship, portrait, star) and track it as you move.
    // When false (default), fire-and-forget: moving clears the gaze.
    public static bool Locked;

    public static void ClearAll()
    {
        EyesOn = BodyOn = HeadOn = false;
        Eyes = Body = Head = default;
    }
}
