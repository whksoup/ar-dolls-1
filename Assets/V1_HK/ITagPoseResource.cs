using System;
using UnityEngine;

using UnityPose = UnityEngine.Pose;

/// <summary>
/// A single sighting of a tag by one camera, at one instant.
///
/// Both the world pose and the camera-relative pose are carried. The world pose
/// is what a consumer normally wants; the camera-relative pose plus the camera
/// pose is what a stereo fusion stage needs, because the useful stereo signal
/// lives in the *bearing* to the tag (well conditioned) rather than in the
/// monocular range estimate (poorly conditioned, and the thing we're trying to
/// replace).
/// </summary>
public readonly struct TagObservation
{
    /// <summary>AprilTag id.</summary>
    public readonly int Id;

    /// <summary>Tag pose in world space.</summary>
    public readonly UnityPose WorldPose;

    /// <summary>Tag pose relative to the observing camera.</summary>
    public readonly UnityPose CameraLocalPose;

    /// <summary>World pose of the observing camera at capture time.</summary>
    public readonly UnityPose CameraPose;

    /// <summary>Capture timestamp of the source image. Use this to pair observations across cameras.</summary>
    public readonly DateTime CaptureTime;

    /// <summary>Time.unscaledTime when the observation was published.</summary>
    public readonly float PublishTime;

    /// <summary>Identifies which camera produced this. 0 = left, 1 = right by convention.</summary>
    public readonly int SourceId;
    //
    public TagObservation(
        int id,
        UnityPose worldPose,
        UnityPose cameraLocalPose,
        UnityPose cameraPose,
        DateTime captureTime,
        float publishTime,
        int sourceId)
    {
        Id = id;
        WorldPose = worldPose;
        CameraLocalPose = cameraLocalPose;
        CameraPose = cameraPose;
        CaptureTime = captureTime;
        PublishTime = publishTime;
        SourceId = sourceId;
    }

    /// <summary>Unit bearing from the camera to the tag origin, in world space. Independent of range calibration.</summary>
    public Vector3 WorldBearing =>
        (CameraPose.rotation * CameraLocalPose.position).normalized;

    /// <summary>Monocular range estimate, in metres.</summary>
    public float Range => CameraLocalPose.position.magnitude;
}

/// <summary>
/// Anything that produces tag observations: a single-camera detector, or a
/// stereo fusion node that consumes two detectors and emits one better pose.
/// Consumers bind to this rather than to a concrete tracker.
/// </summary>
public interface ITagPoseSource
{
    event Action<TagObservation> TagObserved;
}