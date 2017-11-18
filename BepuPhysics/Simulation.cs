﻿using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BepuPhysics
{
    /// <summary>
    /// Orchestrates the bookkeeping and execution of a full dynamic simulation.
    /// </summary>
    public partial class Simulation : IDisposable
    {
        public IslandActivator Activator { get; private set; }
        public Deactivator Deactivator { get; private set; }
        public Bodies Bodies { get; private set; }
        public Statics Statics { get; private set; }
        public Shapes Shapes { get; private set; }
        public BodyLayoutOptimizer BodyLayoutOptimizer { get; private set; }
        public ConstraintLayoutOptimizer ConstraintLayoutOptimizer { get; private set; }
        public BatchCompressor SolverBatchCompressor { get; private set; }
        public Solver Solver { get; private set; }
        public PoseIntegrator PoseIntegrator { get; private set; }
        public BroadPhase BroadPhase { get; private set; }
        public CollidableOverlapFinder BroadPhaseOverlapFinder { get; private set; }
        public NarrowPhase NarrowPhase { get; private set; }



        /// <summary>
        /// Gets the main memory pool used to fill persistent structures and main thread ephemeral resources across the engine.
        /// </summary>
        public BufferPool BufferPool { get; private set; }

        /// <summary>
        /// Gets or sets whether to use a deterministic time step when using multithreading. When set to true, additional time is spent sorting constraint additions and transfers.
        /// Note that this can only affect determinism locally- different processor architectures may implement instructions differently.
        /// </summary>
        public bool Deterministic { get; set; }

        protected Simulation(BufferPool bufferPool, SimulationAllocationSizes initialAllocationSizes)
        {
            BufferPool = bufferPool;
            Statics = new Statics(bufferPool, initialAllocationSizes.Statics);
            Shapes = new Shapes(bufferPool, initialAllocationSizes.ShapesPerType);
            BroadPhase = new BroadPhase(bufferPool, initialAllocationSizes.Bodies, initialAllocationSizes.Bodies + initialAllocationSizes.Statics);
            Activator = new IslandActivator();
            Bodies = new Bodies(bufferPool, Activator, Shapes, BroadPhase,
                initialAllocationSizes.Bodies,
                initialAllocationSizes.Islands, 
                initialAllocationSizes.ConstraintCountPerBodyEstimate);
            
            Solver = new Solver(Bodies, BufferPool,
                initialCapacity: initialAllocationSizes.Constraints,
                minimumCapacityPerTypeBatch: initialAllocationSizes.ConstraintsPerTypeBatch);
            Bodies.Initialize(Solver);
            Deactivator = new Deactivator(Bodies, Solver, BufferPool);
            PoseIntegrator = new PoseIntegrator(Bodies, Shapes, BroadPhase);
            SolverBatchCompressor = new BatchCompressor(Solver, Bodies);
            BodyLayoutOptimizer = new BodyLayoutOptimizer(Bodies, BroadPhase, Solver, bufferPool);
            ConstraintLayoutOptimizer = new ConstraintLayoutOptimizer(Bodies, Solver);
        }

        /// <summary>
        /// Constructs a simulation supporting dynamic movement and constraints with the specified narrow phase callbacks.
        /// </summary>
        /// <param name="bufferPool">Buffer pool used to fill persistent structures and main thread ephemeral resources across the engine.</param>
        /// <param name="narrowPhaseCallbacks">Callbacks to use in the narrow phase.</param>
        /// <param name="initialAllocationSizes">Allocation sizes to initialize the simulation with. If left null, default values are chosen.</param>
        /// <returns>New simulation.</returns>
        public static Simulation Create<TNarrowPhaseCallbacks>(BufferPool bufferPool, TNarrowPhaseCallbacks narrowPhaseCallbacks,
            SimulationAllocationSizes? initialAllocationSizes = null)
            where TNarrowPhaseCallbacks : struct, INarrowPhaseCallbacks
        {
            if (initialAllocationSizes == null)
            {
                initialAllocationSizes = new SimulationAllocationSizes
                {
                    Bodies = 4096,
                    Statics = 4096,
                    ShapesPerType = 128,
                    ConstraintCountPerBodyEstimate = 8,
                    Constraints = 16384,
                    ConstraintsPerTypeBatch = 256
                };
            }

            var simulation = new Simulation(bufferPool, initialAllocationSizes.Value);
            DefaultTypes.Register(simulation.Solver, out var defaultTaskRegistry);
            var narrowPhase = new NarrowPhase<TNarrowPhaseCallbacks>(simulation, defaultTaskRegistry, narrowPhaseCallbacks);
            simulation.NarrowPhase = narrowPhase;
            simulation.BroadPhaseOverlapFinder = new CollidableOverlapFinder<TNarrowPhaseCallbacks>(narrowPhase, simulation.BroadPhase);

            return simulation;
        }


        //TODO: There is an argument for pushing this 'add' and 'remove' stuff into the respective subsystems.
        //The only problem is that they tend to cover multiple subsystems- a body add must deal with the broad phase, constraint graph, and the bodies set.
        //Constraint adds have to deal with the constraint graph and solver.
        //Static adds have to deal with the broad phase and static set.
        //This isn't an unsolvable issue- you can just pass those dependencies in-
        //it's just a question of what would be most reasonable as an API design. Users might default to expecting all body-related stuff to be done within the Bodies,
        //and then they'll get confused when stuff doesn't work like it should when they try making a body kinematic/dynamic by just changing mass or something...
        //It does clearly increase coupling, but I'm not sure it matters. Consider the idea of a 'collision detection only' simulation- 
        //virtually everything goes away. There's no such thing as a 'body' in coldet-only land. You'd just have a tree, collidables within it, and then a stripped down
        //version of the narrowphase that does nothing but report overlaps to a streaming batcher.
        //A 'solver only' simulation is trickier, but I'm not sure it's worth focusing on that because it's effectively just 'don't give any bodies a collidable'.
        //Or you could explicitly disable the broadphase/overlapfinder/narrowphases, leaving the rest unchanged.
        //(And then you could say, oh, but what about a solver-only simulation *that doesn't support deactivation!* and frankly it's just getting a little absurd.)
        //Forcing the main simulation API to jump through awkward hoops to maintain phantasmal decoupling just seems... questionable.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateBounds(ref RigidPose pose, ref TypedIndex shapeIndex, out BoundingBox bodyBounds)
        {
            //Note: the min and max here are in absolute coordinates, which means this is a spot that has to be updated in the event that positions use a higher precision representation.
            Shapes[shapeIndex.Type].ComputeBounds(shapeIndex.Index, ref pose, out bodyBounds.Min, out bodyBounds.Max);
        }

        //    STATICS 
        public int Add(ref StaticDescription description)
        {
            var handle = Statics.Add(ref description);
            var index = Statics.HandleToIndex[handle];
            Debug.Assert(description.Collidable.Shape.Exists, "Static collidables cannot lack a shape. Their only purpose is colliding.");
            //Note that we have to calculate an initial bounding box for the broad phase to be able to insert it efficiently.
            //(In the event of batch adds, you'll want to use batched AABB calculations or just use cached values.)
            //Note: the min and max here are in absolute coordinates, which means this is a spot that has to be updated in the event that positions use a higher precision representation.
            UpdateBounds(ref description.Pose, ref description.Collidable.Shape, out var bounds);
            //Note that new body collidables are always assumed to be active.
            Statics.Collidables[index].BroadPhaseIndex =
                BroadPhase.AddStatic(new CollidableReference(CollidableMobility.Static, handle), ref bounds);
            return handle;
        }
        public void ApplyDescription(int handle, ref StaticDescription description)
        {
            Statics.ValidateExistingHandle(handle);
            var bodyIndex = Statics.HandleToIndex[handle];
            ref var collidable = ref Statics.Collidables[bodyIndex];
            Debug.Assert(description.Collidable.Shape.Exists, "Static collidables cannot lack a shape. Their only purpose is colliding.");
            Statics.SetDescriptionByIndex(bodyIndex, ref description);
        }

        public void RemoveStatic(int handle)
        {
            Statics.ValidateExistingHandle(handle);

            var bodyIndex = Statics.HandleToIndex[handle];
            ref var collidable = ref Statics.Collidables[bodyIndex];
            Debug.Assert(collidable.Shape.Exists, "Static collidables cannot lack a shape. Their only purpose is colliding.");

            var removedBroadPhaseIndex = collidable.BroadPhaseIndex;
            if (BroadPhase.RemoveStaticAt(removedBroadPhaseIndex, out var movedLeaf))
            {
                //When a leaf is removed from the broad phase, another leaf will move to take its place in the leaf set.
                //We must update the collidable->leaf index pointer to match the new position of the leaf in the broadphase.
                //There are two possible cases for the moved leaf:
                //1) it is an inactive body collidable,
                //2) it is a static collidable.
                //The collidable reference we retrieved tells us whether it's a body or a static.
                //In the event that it's a body, we can infer the activity state from the body we just removed. Any body within the same 'leaf space' as the removed body
                //shares its activity state. This involves some significant conceptual coupling with the broad phase's implementation, but that's a price we're willing to pay
                //if it avoids extraneous data storage.
                if (movedLeaf.Mobility == CollidableMobility.Static)
                {
                    //This is a static collidable, not a body.
                    Statics.Collidables[Statics.HandleToIndex[movedLeaf.Handle]].BroadPhaseIndex = removedBroadPhaseIndex;
                }
                else
                {
                    //This is an inactive body.
                    ref var location = ref Bodies.HandleToLocation[movedLeaf.Handle];
                    Bodies.Sets[location.SetIndex].Collidables[location.Index].BroadPhaseIndex = removedBroadPhaseIndex;
                }
            }

            Statics.RemoveAt(bodyIndex, out var movedStaticOriginalIndex);
        }



        //     CONSTRAINTS

        /// <summary>
        /// Allocates a constraint slot and sets up a constraint with the specified description.
        /// </summary>
        /// <typeparam name="TDescription">Type of the constraint description to add.</typeparam>
        /// <param name="bodyHandles">First body handle in a list of body handles used by the constraint.</param>
        /// <param name="bodyCount">Number of bodies used by the constraint.</param>
        /// <returns>Allocated constraint handle.</returns>
        public int Add<TDescription>(ref int bodyHandles, int bodyCount, ref TDescription description)
            where TDescription : IConstraintDescription<TDescription>
        {
            Solver.Add(ref bodyHandles, bodyCount, ref description, out int constraintHandle);
            for (int i = 0; i < bodyCount; ++i)
            {
                var bodyHandle = Unsafe.Add(ref bodyHandles, i);
                Bodies.ValidateExistingHandle(bodyHandle);
                Bodies.AddConstraint(Bodies.HandleToLocation[bodyHandle].Index, constraintHandle, i);
            }
            return constraintHandle;
        }

        /// <summary>
        /// Allocates a two-body constraint slot and sets up a constraint with the specified description.
        /// </summary>
        /// <typeparam name="TDescription">Type of the constraint description to add.</typeparam>
        /// <param name="bodyHandleA">First body of the pair.</param>
        /// <param name="bodyHandleB">Second body of the pair.</param>
        /// <returns>Allocated constraint handle.</returns>
        public unsafe int Add<TDescription>(int bodyHandleA, int bodyHandleB, ref TDescription description)
            where TDescription : IConstraintDescription<TDescription>
        {
            //Don't really want to take a dependency on the stack layout of parameters, so...
            var bodyReferences = stackalloc int[2];
            bodyReferences[0] = bodyHandleA;
            bodyReferences[1] = bodyHandleB;
            return Add(ref bodyReferences[0], 2, ref description);
        }


        public void RemoveConstraint(int constraintHandle)
        {
            ConstraintGraphRemovalEnumerator enumerator;
            enumerator.bodies = Bodies;
            enumerator.constraintHandle = constraintHandle;
            Solver.EnumerateConnectedBodies(constraintHandle, ref enumerator);
            Solver.Remove(constraintHandle);
        }

        //TODO: I wonder if people will abuse the dt-as-parameter to the point where we should make it a field instead, like it effectively was in v1.
        /// <summary>
        /// Performs one timestep of the given length.
        /// </summary>
        /// <remarks>
        /// Be wary of variable timesteps. They can harm stability. Whenever possible, keep the timestep the same across multiple frames unless you have a specific reason not to.
        /// </remarks>
        /// <param name="dt">Duration of the time step in time.</param>
        public void Timestep(float dt, IThreadDispatcher threadDispatcher = null)
        {
            ProfilerClear();
            ProfilerStart(this);
            //Note that the first behavior-affecting stage is actually the pose integrator. This is a shift from v1, where collision detection went first.
            //This is a tradeoff:
            //1) Any externally set velocities will be integrated without input from the solver. The v1-style external velocity control won't work as well-
            //the user would instead have to change velocities after the pose integrator runs. This isn't perfect either, since the pose integrator is also responsible
            //for updating the bounding boxes used for collision detection.
            //2) By bundling bounding box calculation with pose integration, you avoid redundant pose and velocity memory accesses.
            //3) Generated contact positions are in sync with the integrated poses. 
            //That's often helpful for gameplay purposes- you don't have to reinterpret contact data when creating graphical effects or positioning sound sources.

            //TODO: This is something that is possibly worth exposing as one of the generic type parameters. Users could just choose the order arbitrarily.
            //Or, since you're talking about something that happens once per frame instead of once per collision pair, just provide a simple callback.
            //(Or maybe an enum even?)
            //#1 is a difficult problem, though. There is no fully 'correct' place to change velocities. We might just have to bite the bullet and create a
            //inertia tensor/bounding box update separate from pose integration. If the cache gets evicted in between (virtually guaranteed unless no stages run),
            //this basically means an extra 100-200 microseconds per frame on a processor with ~20GBps bandwidth simulating 32768 bodies.

            //Note that the reason why the pose integrator comes first instead of, say, the solver, is that the solver relies on world space inertias calculated by the pose integration.
            //If the pose integrator doesn't run first, we either need 
            //1) complicated on demand updates of world inertia when objects are added or local inertias are changed or 
            //2) local->world inertia calculation before the solver.        

            ProfilerStart(PoseIntegrator);
            PoseIntegrator.Update(dt, BufferPool, threadDispatcher);
            ProfilerEnd(PoseIntegrator);

            ProfilerStart(BroadPhase);
            BroadPhase.Update(threadDispatcher);
            ProfilerEnd(BroadPhase);

            ProfilerStart(BroadPhaseOverlapFinder);
            BroadPhaseOverlapFinder.DispatchOverlaps(threadDispatcher);
            ProfilerEnd(BroadPhaseOverlapFinder);

            ProfilerStart(NarrowPhase);
            NarrowPhase.Flush(threadDispatcher, threadDispatcher != null && Deterministic);
            ProfilerEnd(NarrowPhase);

            ProfilerStart(Solver);
            if (threadDispatcher == null)
                Solver.Update(dt);
            else
                Solver.MultithreadedUpdate(threadDispatcher, BufferPool, dt);
            ProfilerEnd(Solver);

            //Note that constraint optimization should be performed after body optimization, since body optimization moves the bodies- and so affects the optimal constraint position.
            //TODO: The order of these optimizer stages is performance relevant, even though they don't have any effect on correctness.
            //You may want to try them in different locations to see how they impact cache residency.
            ProfilerStart(BodyLayoutOptimizer);
            if (threadDispatcher == null)
                BodyLayoutOptimizer.IncrementalOptimize();
            else
                BodyLayoutOptimizer.IncrementalOptimize(BufferPool, threadDispatcher);
            ProfilerEnd(BodyLayoutOptimizer);

            ProfilerStart(ConstraintLayoutOptimizer);
            ConstraintLayoutOptimizer.Update(BufferPool, threadDispatcher);
            ProfilerEnd(ConstraintLayoutOptimizer);

            ProfilerStart(SolverBatchCompressor);
            SolverBatchCompressor.Compress(BufferPool, threadDispatcher, threadDispatcher != null && Deterministic);
            ProfilerEnd(SolverBatchCompressor);

            ProfilerEnd(this);
        }

        /// <summary>
        /// Clears the simulation of every object, only returning memory to the pool that would be returned by sequential removes. 
        /// Other persistent allocations, like those in the Bodies set, will remain.
        /// </summary>
        public void Clear()
        {
            Solver.Clear();
            Bodies.Clear();
            Statics.Clear();
            Shapes.Clear();
            BroadPhase.Clear();
        }

        /// <summary>
        /// Increases the allocation size of any buffers too small to hold the allocation target.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The final size of the allocated buffers are constrained by the allocator. It is not guaranteed to be exactly equal to the target, but it is guaranteed to be at least as large.
        /// </para>
        /// <para>
        /// This is primarily a convenience function. Everything it does internally can be done externally.
        /// For example, if only type batches need to be resized, the solver's own functions can be used directly.
        /// </para>
        /// </remarks>
        /// <param name="allocationTarget">Allocation sizes to guarantee sufficient size for.</param>
        public void EnsureCapacity(SimulationAllocationSizes allocationTarget)
        {
            Solver.EnsureSolverCapacities(allocationTarget.Bodies, allocationTarget.Constraints);
            Solver.MinimumCapacityPerTypeBatch = Math.Max(allocationTarget.ConstraintsPerTypeBatch, Solver.MinimumCapacityPerTypeBatch);
            Solver.EnsureTypeBatchCapacities();
            //Note that the bodies set has to come before the body layout optimizer; the body layout optimizer's sizes are dependent upon the bodies set.
            Bodies.EnsureCapacity(allocationTarget.Bodies);
            Bodies.MinimumConstraintCapacityPerBody = allocationTarget.ConstraintCountPerBodyEstimate;
            Bodies.EnsureConstraintListCapacities();
            BodyLayoutOptimizer.ResizeForBodiesCapacity(BufferPool);
            Statics.EnsureCapacity(allocationTarget.Statics);
            Shapes.EnsureBatchCapacities(allocationTarget.ShapesPerType);
            BroadPhase.EnsureCapacity(allocationTarget.Bodies, allocationTarget.Bodies + allocationTarget.Statics);
        }


        /// <summary>
        /// Increases the allocation size of any buffers too small to hold the allocation target, and decreases the allocation size of any buffers that are unnecessarily large.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The final size of the allocated buffers are constrained by the allocator. It is not guaranteed to be exactly equal to the target, but it is guaranteed to be at least as large.
        /// </para>
        /// <para>
        /// This is primarily a convenience function. Everything it does internally can be done externally.
        /// For example, if only type batches need to be resized, the solver's own functions can be used directly.
        /// </para>
        /// </remarks>
        /// <param name="allocationTarget">Allocation sizes to guarantee sufficient size for.</param>
        public void Resize(SimulationAllocationSizes allocationTarget)
        {
            Solver.ResizeSolverCapacities(allocationTarget.Bodies, allocationTarget.Constraints);
            Solver.MinimumCapacityPerTypeBatch = allocationTarget.ConstraintsPerTypeBatch;
            Solver.ResizeTypeBatchCapacities();
            //Note that the bodies set has to come before the body layout optimizer; the body layout optimizer's sizes are dependent upon the bodies set.
            Bodies.Resize(allocationTarget.Bodies);
            Bodies.MinimumConstraintCapacityPerBody = allocationTarget.ConstraintCountPerBodyEstimate;
            Bodies.ResizeConstraintListCapacities();
            BodyLayoutOptimizer.ResizeForBodiesCapacity(BufferPool);
            Statics.Resize(allocationTarget.Statics);
            Shapes.ResizeBatches(allocationTarget.ShapesPerType);
            BroadPhase.Resize(allocationTarget.Bodies, allocationTarget.Bodies + allocationTarget.Statics);
        }

        /// <summary>
        /// Clears the simulation of every object and returns all pooled memory to the buffer pool. Leaves the simulation in an unusable state.
        /// </summary>
        public void Dispose()
        {
            Clear();
            Solver.Dispose();
            BroadPhase.Dispose();
            NarrowPhase.Dispose();
            Bodies.Dispose();
            Statics.Dispose();
            BodyLayoutOptimizer.Dispose(BufferPool);
            Shapes.Dispose();
        }
    }
}
