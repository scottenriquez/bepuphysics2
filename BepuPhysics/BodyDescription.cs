﻿using BepuPhysics.Collidables;
using System.Numerics;

namespace BepuPhysics
{
    public struct RigidPose
    {
        public Vector3 Position;
        public BepuUtilities.Quaternion Orientation;
    }

    public struct BodyVelocity
    {
        public Vector3 Linear;
        public Vector3 Angular;
    }
    public struct BodyInertia
    {
        public Triangular3x3 InverseInertiaTensor;
        public float InverseMass;
    }
    public struct BodyActivity
    {
        /// <summary>
        /// Threshold of squared velocity under which the body is allowed to go inactive. This is compared against dot(linearVelocity, linearVelocity) + dot(angularVelocity, angularVelocity).
        /// Setting this to a negative value guarantees the body cannot go inactive without user action.
        /// </summary>
        public float DeactivationThreshold;
        /// <summary>
        /// The number of time steps that the body must be under the deactivation threshold before the body becomes a deactivation candidate.
        /// Note that the body is not guaranteed to go inactive immediately after meeting this minimum.
        /// </summary>
        public byte MinimumTimestepsUnderThreshold;

        //Note that all values beyond this point are runtime set. The user should virtually never need to modify them. 
        //We do not constrain write access by default, instead opting to leave it open for advanced users to mess around with.
        //TODO: If people misuse these, we should internalize them in a case by case basis. Kinematic and DeactivationCandidate are two likely

        //TODO: We may later decide to encode kinematic-ness in the indices held by constraints. That would be somewhat complicated, but if we end up using such an encoding
        //to avoid velocity reads from/writes to kinematics in the solver, we might as well use it for the traversal too.
        /// <summary>
        /// True if this body has effectively infinite mass and inertia, false otherwise. Kinematic bodies block constraint graph traversals since they cannot propagate impulses.
        /// This value should remain in sync with other systems that make a distinction between kinematic and dynamic bodies. Under normal circumstances, it should never be externally set.
        /// To change an object's kinematic/dynamic state, use Bodies.ChangeLocalInertia or Bodies.ApplyDescription.
        /// </summary>
        public bool Kinematic;
        /// <summary>
        /// If the body is active, this is the number of time steps that the body has had a velocity below the deactivation threshold.
        /// </summary>
        public byte TimestepsUnderThresholdCount;
        //Note that this flag is held alongside the other deactivation data, despite the fact that the traversal only needs the deactivation state.
        //This is primarily for simplicity, but also note that the dominant accessor of this field is actually the deactivation candidacy computation. Traversal doesn't visit every
        //body every frame, but deactivation candidacy analysis does.
        //The reason why this flag exists at all is just to prevent traversal from being aware of the logic behind candidacy managemnt.
        //It doesn't cost anything extra to store this; it fits within the 8 byte layout.
        /// <summary>
        /// True if this body is a candidate for being deactivated. If all the bodies that it is connected to by constraints are also candidates, this body may go to sleep.
        /// </summary>
        /// <remarks>
        /// This property acts as a firewall for deactivation logic. The Deactivator doesn't need to know about the rest of what constitutes candidacy.
        /// You
        /// </remarks>
        public bool DeactivationCandidate;
    }

    public struct BodyActivityDescription
    {
        /// <summary>
        /// Threshold of squared velocity under which the body is allowed to go inactive. This is compared against dot(linearVelocity, linearVelocity) + dot(angularVelocity, angularVelocity).
        /// </summary>
        public float DeactivationThreshold;
        /// <summary>
        /// The number of time steps that the body must be under the deactivation threshold before the body becomes a deactivation candidate.
        /// Note that the body is not guaranteed to go inactive immediately after meeting this minimum.
        /// </summary>
        public byte MinimumTimestepCountUnderThreshold;

    }


    public struct BodyDescription
    {
        public RigidPose Pose;
        public BodyInertia LocalInertia;
        public BodyVelocity Velocity;
        public CollidableDescription Collidable;
        public BodyActivityDescription Activity;

        /// <summary>
        /// Gets the mobility state for a collidable based on this body description's mass and inertia tensor.
        /// If all components of inverse mass and inverse inertia are zero, it is kinematic; otherwise, it is dynamic.
        /// </summary>
        public CollidableMobility Mobility
        {
            get
            {
                return (LocalInertia.InverseMass == 0 &&
                        LocalInertia.InverseInertiaTensor.M11 == 0 &&
                        LocalInertia.InverseInertiaTensor.M21 == 0 &&
                        LocalInertia.InverseInertiaTensor.M22 == 0 &&
                        LocalInertia.InverseInertiaTensor.M31 == 0 &&
                        LocalInertia.InverseInertiaTensor.M32 == 0 &&
                        LocalInertia.InverseInertiaTensor.M33 == 0) ? CollidableMobility.Kinematic : CollidableMobility.Dynamic;
            }
        }

    }

    public struct StaticDescription
    {
        public RigidPose Pose;
        public CollidableDescription Collidable;
    }

    public struct RigidPoses
    {
        public Vector3Wide Position;
        //Note that we store a quaternion rather than a matrix3x3. While this often requires some overhead when performing vector transforms or extracting basis vectors, 
        //systems needing to interact directly with this representation are often terrifically memory bound. Spending the extra ALU time to convert to a basis can actually be faster
        //than loading the extra 5 elements needed to express the full 3x3 rotation matrix. Also, it's marginally easier to keep the rotation normalized over time.
        //There may be an argument for the matrix variant to ALSO be stored for some bandwidth-unconstrained stages, but don't worry about that until there's a reason to worry about it.
        public QuaternionWide Orientation;
    }

    public struct BodyVelocities
    {
        public Vector3Wide LinearVelocity;
        public Vector3Wide AngularVelocity;
    }

    public struct BodyInertias
    {
        public Triangular3x3Wide InverseInertiaTensor;
        //Note that the inverse mass is included in the BodyInertias bundle. InverseMass is rotationally invariant, so it doesn't need to be updated...
        //But it's included alongside the rotated inertia tensor because to split it out would require that constraint presteps suffer another cache miss when they
        //gather the inverse mass in isolation. (From the solver's perspective, inertia/mass gathering is incoherent.)
        public Vector<float> InverseMass;
    }
}
