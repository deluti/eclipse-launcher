using System.Numerics;

namespace Eclipse;

internal enum StandingPose { HandAtHead, Relaxed }
internal enum SkinBone { Torso, Head, RightUpperArm, RightForearm, LeftUpperArm, LeftForearm, RightThigh, RightShin, LeftThigh, LeftShin, Cape }

internal sealed class SkinPoseRig
{
    internal static readonly Vector3 CapeAnchor = new(0, 23, -3.5f);
    internal const int CapeWidth = 10;
    internal const int CapeHeight = 16;
    internal const int CapeThickness = 1;
    internal readonly Matrix4x4[] Transforms = new Matrix4x4[11];
    internal StandingPose Pose { get; private set; }
    internal double Seconds { get; private set; }
    private readonly float phase;
    private float armWidth = 4;

    internal SkinPoseRig(StandingPose pose, float phase = 0) { Pose = pose; this.phase = phase; Sample(); }
    internal void SetPose(StandingPose pose) { Pose = pose; Seconds = 0; Sample(); }
    internal void SetArmWidth(float width) { armWidth = width; Sample(); }
    internal void Advance(double elapsed) { Seconds += Math.Clamp(elapsed, 0, .05); Sample(); }
    private static float Radians(float degrees) => degrees * (MathF.PI / 180);
    private static Matrix4x4 Joint(Vector3 pivot, float x, float y, float z) =>
        Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateRotationX(Radians(x)) *
        Matrix4x4.CreateRotationY(Radians(y)) * Matrix4x4.CreateRotationZ(Radians(z)) * Matrix4x4.CreateTranslation(pivot);

    private void Sample()
    {
        var t = (float)Seconds;
        var breath = MathF.Sin(t * (2 * MathF.PI / 4.4f) + phase);
        var sway = MathF.Sin(t * (2 * MathF.PI / 6.8f) + phase + .6f);
        var head = MathF.Sin(t * (2 * MathF.PI / 5.7f) + phase + 1.2f);
        var raised = Pose == StandingPose.HandAtHead;
        var breathing = Matrix4x4.CreateTranslation(0, -12, 0) * Matrix4x4.CreateScale(1 + breath * .002f, 1 + breath * .006f, 1 + breath * .003f) * Matrix4x4.CreateTranslation(0, 12, 0);
        var torso = breathing * Joint(new(0, 12, 0), raised ? -2 : -7, raised ? -3 : 3,
            (raised ? -3.5f : 4.5f) + sway * .65f) * Matrix4x4.CreateTranslation(sway * .07f, 0, 0);
        Transforms[(int)SkinBone.Torso] = torso;
        Transforms[(int)SkinBone.Head] = Joint(new(0, 24, 0), 1 + breath * .7f,
            (raised ? -7 : 5) + head * 1.8f, (raised ? 4 : -5) + sway * .5f) * torso;

        var shoulderX = 4 + armWidth / 2;
        var rightShoulder = new Vector3(-shoulderX, 24, 0);
        var leftShoulder = new Vector3(shoulderX, 24, 0);
        var rightArm = Joint(rightShoulder, raised ? -3 + breath : -15 + breath,
            raised ? 0 : -5, -8 + sway * .9f) * torso;
        var leftArm = Joint(leftShoulder, raised ? -8 : 4 + breath,
            raised ? -6 : 0, raised ? 109 + sway * .6f : 7 + sway * .9f) * torso;
        Transforms[(int)SkinBone.RightUpperArm] = rightArm;
        Transforms[(int)SkinBone.RightForearm] = Joint(new(-shoulderX, 18, 0), raised ? -5 : -12, 0, raised ? -2 : -4) * rightArm;
        Transforms[(int)SkinBone.LeftUpperArm] = leftArm;
        Transforms[(int)SkinBone.LeftForearm] = Joint(new(shoulderX, 18, 0), raised ? -5 + breath * .4f : -3,
            0, raised ? 115 + breath * .45f : 2) * leftArm;

        var rightLeg = Joint(new(-2, 12, 0), raised ? -2 : -4, raised ? 1 : -3, raised ? -4 : -8);
        var leftLeg = Joint(new(2, 12, 0), raised ? -3 : -2, raised ? -3 : 3, raised ? 11 : 12);
        Transforms[(int)SkinBone.RightThigh] = rightLeg;
        Transforms[(int)SkinBone.RightShin] = Joint(new(-2, 6, 0), raised ? 3 : 6, 0, 0) * rightLeg;
        Transforms[(int)SkinBone.LeftThigh] = leftLeg;
        Transforms[(int)SkinBone.LeftShin] = Joint(new(2, 6, 0), raised ? 4 : 3, 0, 0) * leftLeg;
        // The clasp follows the upper back; gravity keeps the cloth vertical in world space.
        // Inheriting torso pitch would swing the lower edge forward through the legs.
        var capePosition = Vector3.Transform(CapeAnchor, torso);
        var shoulderDirection = Vector3.TransformNormal(Vector3.UnitX, torso);
        var capeYaw = MathF.Atan2(-shoulderDirection.Z, shoulderDirection.X);
        Transforms[(int)SkinBone.Cape] = Matrix4x4.CreateTranslation(-CapeAnchor) *
            Matrix4x4.CreateRotationX(Radians(2.5f + sway * .5f)) * Matrix4x4.CreateRotationY(capeYaw) *
            Matrix4x4.CreateTranslation(capePosition);

        // Keep the lowest foot corner on the platform instead of letting idle motion float the body.
        var lowest = float.MaxValue;
        for (var leg = 0; leg < 2; leg++)
        {
            var bone = leg == 0 ? SkinBone.RightShin : SkinBone.LeftShin;
            var cx = bone == SkinBone.RightShin ? -2 : 2;
            for (var x = -2; x <= 2; x += 4)
            for (var y = 0; y <= 6; y += 6)
            for (var z = -2; z <= 2; z += 4)
                lowest = Math.Min(lowest, Vector3.Transform(new(cx + x, y, z), Transforms[(int)bone]).Y);
        }
        var grounding = Matrix4x4.CreateTranslation(0, -lowest, 0);
        for (var i = 0; i < Transforms.Length; i++) Transforms[i] *= grounding;
    }
}
