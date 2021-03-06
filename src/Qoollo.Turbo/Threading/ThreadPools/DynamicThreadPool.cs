﻿using Qoollo.Turbo.Threading.ThreadPools.Common;
using Qoollo.Turbo.Threading.ThreadPools.ServiceStuff;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Qoollo.Turbo.Threading.ThreadPools
{
    /// <summary>
    /// Configuration parameters for <see cref="DynamicThreadPool"/>
    /// </summary>
    public class DynamicThreadPoolOptions
    {
        /// <summary>
        /// Default parameters
        /// </summary>
        internal static readonly DynamicThreadPoolOptions Default = new DynamicThreadPoolOptions();

        /// <summary>
        /// Default period in milliseconds when unused threads will be removed from ThreadPool
        /// </summary>
        public const int DefaultNoWorkItemTrimPeriod = 5 * 60 * 1000;
        /// <summary>
        /// Default period of sleep in milliseconds between checking for the possibility to steal a work item from local queues
        /// </summary>
        public const int DefaultQueueStealAwakePeriod = 2000;
        /// <summary>
        /// Default maximum queue capacity extension
        /// </summary>
        public const int DefaultMaxQueueCapacityExtension = 256;
        /// <summary>
        /// Default priod in milliseconds when the management thread is checks the state of the pool and changes the number of threads
        /// </summary>
        public const int DefaultManagementProcessPeriod = 500;
        /// <summary>
        /// Default value indicating whether or not set ThreadPool TaskScheduler as a default for all ThreadPool threads
        /// </summary>
        public const bool DefaultUseOwnTaskScheduler = false;
        /// <summary>
        /// Default value indicating whether or not set ThreadPool SynchronizationContext as a default for all ThreadPool threads
        /// </summary>
        public const bool DefaultUseOwnSyncContext = false;
        /// <summary>
        /// Default value indicating whether or not to flow ExecutionContext to the ThreadPool thread
        /// </summary>
        public const bool DefaultFlowExecutionContext = false;

        /// <summary>
        /// <see cref="DynamicThreadPoolOptions"/> constructor
        /// </summary>
        public DynamicThreadPoolOptions()
        {
            NoWorkItemTrimPeriod = DefaultNoWorkItemTrimPeriod;
            QueueStealAwakePeriod = DefaultQueueStealAwakePeriod;
            ManagementProcessPeriod = DefaultManagementProcessPeriod;
            MaxQueueCapacityExtension = DefaultMaxQueueCapacityExtension;
            UseOwnTaskScheduler = DefaultUseOwnTaskScheduler;
            UseOwnSyncContext = DefaultUseOwnSyncContext;
            FlowExecutionContext = DefaultFlowExecutionContext;
        }

        /// <summary>
        /// Gets or sets the period in milliseconds when unused and suspended threads will be removed from ThreadPool (if less than zero then threads are never removed)
        /// </summary>
        public int NoWorkItemTrimPeriod { get; set; }
        /// <summary>
        /// Gets or sets period of sleep in milliseconds between checking for the possibility to steal a work item from local queues
        /// </summary>
        public int QueueStealAwakePeriod { get; set; }
        /// <summary>
        /// Gets or sets the maximum queue capacity extension
        /// </summary>
        public int MaxQueueCapacityExtension { get; set; }
        /// <summary>
        /// Gets or sets the period in milliseconds when the management procedure checks the state of the ThreadPool and changes the number of active threads
        /// </summary>
        public int ManagementProcessPeriod { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether or not to set ThreadPool TaskScheduler as a default for all ThreadPool threads
        /// </summary>
        public bool UseOwnTaskScheduler { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether or not to set ThreadPool SynchronizationContext as a default for all ThreadPool threads
        /// </summary>
        public bool UseOwnSyncContext { get; set; }
        /// <summary>
        /// Gets or sets a value indicating whether or not to flow ExecutionContext to the ThreadPool thread
        /// </summary>
        public bool FlowExecutionContext { get; set; }      
    }

    /// <summary>
    /// ThreadPool that automatically changes the number of running threads in accordance with the workload
    /// </summary>
    public class DynamicThreadPool: Common.CommonThreadPool
    {
        internal static bool DisableCritical = false;

        private const int WorkItemPerThreadLimit = 32;
        private const int NoWorkItemPreventDeactivationPeriod = 2 * 1000;
        /// <summary>
        /// Upper limit of the number of threads until that the simplified thread spawning logic can be applied (see <see cref="TryRequestNewThreadOnAdd"/>)
        /// </summary>
        private static readonly int FastSpawnThreadCountLimit = Environment.ProcessorCount <= 2 ? Environment.ProcessorCount : Environment.ProcessorCount / 2;
        private static readonly int ReasonableThreadCount = Environment.ProcessorCount;


        private readonly int _minThreadCount;
        private readonly int _maxThreadCount;

        private readonly int _fastSpawnThreadCountLimit;
        private readonly int _reasonableThreadCount;

        private readonly int _managementProcessPeriod;
        private readonly int _maxQueueCapacityExtension;
        private readonly int _noWorkItemTrimPeriod;

        private readonly ExecutionThroughoutTrackerUpDownCorrection _throughoutTracker;
        private volatile bool _wasSomeProcessByThreadsFlag;

        /// <summary>
        /// Composite variable that stores multiple values inside. This is necessary for atomic updating of all values.
        /// High 8 bit - DieSlot (number of requests to stop threads)
        /// Next 12 bit - ActiveThreadCount (active number of threads)
        /// Rest 12 bit - FullThreadCount (total number of threads (active and blocked))
        /// </summary>
        private int _dieSlotActiveFullThreadCountCombination;

        private readonly PartialThreadBlocker _extThreadBlocker;

        private readonly object _syncObject = new object();


        /// <summary>
        /// <see cref="DynamicThreadPool"/> constructor
        /// </summary>
        /// <param name="minThreadCount">Minimum number of threads that should always be in pool</param>
        /// <param name="maxThreadCount">Maximum number of threads in pool</param>
        /// <param name="queueBoundedCapacity">The bounded size of the work items queue (if less or equal to 0 then no limitation)</param>
        /// <param name="name">The name for this instance of ThreadPool and for its threads</param>
        /// <param name="isBackground">Whether or not threads are a background threads</param>
        /// <param name="options">Additional thread pool creation parameters</param>
        private DynamicThreadPool(DynamicThreadPoolOptions options, int minThreadCount, int maxThreadCount, int queueBoundedCapacity, string name, bool isBackground)
            : base(queueBoundedCapacity, options.QueueStealAwakePeriod, isBackground, name, options.UseOwnTaskScheduler, options.UseOwnSyncContext, options.FlowExecutionContext)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (minThreadCount < 0)
                throw new ArgumentOutOfRangeException(nameof(minThreadCount));
            if (maxThreadCount <= 0 || maxThreadCount >= 4096)
                throw new ArgumentOutOfRangeException(nameof(maxThreadCount), "maxThreadCount should be in range [0, 4096)");
            if (maxThreadCount < minThreadCount)
                throw new ArgumentOutOfRangeException(nameof(maxThreadCount), "maxThreadCount should be greater than minThreadCount");
            if (options.MaxQueueCapacityExtension < 0)
                throw new ArgumentOutOfRangeException(nameof(options.MaxQueueCapacityExtension));
            if (options.ManagementProcessPeriod <= 0)
                throw new ArgumentOutOfRangeException(nameof(options.ManagementProcessPeriod));

            _minThreadCount = minThreadCount;
            _maxThreadCount = maxThreadCount;

            _fastSpawnThreadCountLimit = Math.Max(_minThreadCount, Math.Min(_maxThreadCount, FastSpawnThreadCountLimit));
            _reasonableThreadCount = Math.Max(_minThreadCount, Math.Min(_maxThreadCount, ReasonableThreadCount));

            _managementProcessPeriod = options.ManagementProcessPeriod;
            _maxQueueCapacityExtension = options.MaxQueueCapacityExtension;
            _noWorkItemTrimPeriod = options.NoWorkItemTrimPeriod >= 0 ? options.NoWorkItemTrimPeriod : -1;

            _throughoutTracker = new ExecutionThroughoutTrackerUpDownCorrection(_maxThreadCount, _reasonableThreadCount);
            _wasSomeProcessByThreadsFlag = false;

            _dieSlotActiveFullThreadCountCombination = 0;

            _extThreadBlocker = new PartialThreadBlocker(0);

            Qoollo.Turbo.Threading.ServiceStuff.ManagementThreadController.Instance.RegisterCallback(ManagementThreadProc);

            FillPoolUpTo(minThreadCount);
        }
        /// <summary>
        /// <see cref="DynamicThreadPool"/> constructor
        /// </summary>
        /// <param name="minThreadCount">Minimum number of threads that should always be in pool</param>
        /// <param name="maxThreadCount">Maximum number of threads in pool</param>
        /// <param name="queueBoundedCapacity">The bounded size of the work items queue (if less or equal to 0 then no limitation)</param>
        /// <param name="name">The name for this instance of ThreadPool and for its threads</param>
        /// <param name="isBackground">Whether or not threads are a background threads</param>
        /// <param name="options">Additional thread pool creation parameters</param>
        public DynamicThreadPool(int minThreadCount, int maxThreadCount, int queueBoundedCapacity, string name, bool isBackground, DynamicThreadPoolOptions options)
            : this(options ?? DynamicThreadPoolOptions.Default, minThreadCount, maxThreadCount, queueBoundedCapacity, name, isBackground)
        {
        }
        /// <summary>
        /// <see cref="DynamicThreadPool"/> constructor
        /// </summary>
        /// <param name="minThreadCount">Minimum number of threads that should always be in pool</param>
        /// <param name="maxThreadCount">Maximum number of threads in pool</param>
        /// <param name="queueBoundedCapacity">The bounded size of the work items queue (if less or equal to 0 then no limitation)</param>
        /// <param name="name">The name for this instance of ThreadPool and for its threads</param>
        /// <param name="isBackground">Whether or not threads are a background threads</param>
        public DynamicThreadPool(int minThreadCount, int maxThreadCount, int queueBoundedCapacity, string name, bool isBackground)
            : this(DynamicThreadPoolOptions.Default, minThreadCount, maxThreadCount, queueBoundedCapacity, name, isBackground)
        {
        }
        /// <summary>
        /// <see cref="DynamicThreadPool"/> constructor
        /// </summary>
        /// <param name="minThreadCount">Minimum number of threads that should always be in pool</param>
        /// <param name="maxThreadCount">Maximum number of threads in pool</param>
        /// <param name="queueBoundedCapacity">The bounded size of the work items queue (if less or equal to 0 then no limitation)</param>
        /// <param name="name">The name for this instance of ThreadPool and for its threads</param>
        public DynamicThreadPool(int minThreadCount, int maxThreadCount, int queueBoundedCapacity, string name)
            : this(DynamicThreadPoolOptions.Default, minThreadCount, maxThreadCount, queueBoundedCapacity, name, false)
        {
        }
        /// <summary>
        /// <see cref="DynamicThreadPool"/> constructor
        /// </summary>
        /// <param name="minThreadCount">Minimum number of threads that should always be in pool</param>
        /// <param name="maxThreadCount">Maximum number of threads in pool</param>
        /// <param name="name">The name for this instance of ThreadPool and for its threads</param>
        public DynamicThreadPool(int minThreadCount, int maxThreadCount, string name)
            : this(DynamicThreadPoolOptions.Default, minThreadCount, maxThreadCount, -1, name, false)
        {
        }
        /// <summary>
        /// <see cref="DynamicThreadPool"/> constructor
        /// </summary>
        /// <param name="maxThreadCount">Maximum number of threads in pool</param>
        /// <param name="name">The name for this instance of ThreadPool and for its threads</param>
        public DynamicThreadPool(int maxThreadCount, string name)
            : this(DynamicThreadPoolOptions.Default, 0, maxThreadCount, -1, name, false)
        {
        }
        /// <summary>
        /// <see cref="DynamicThreadPool"/> constructor
        /// </summary>
        /// <param name="name">The name for this instance of ThreadPool and for its threads</param>
        public DynamicThreadPool(string name)
            : this(DynamicThreadPoolOptions.Default, 0, 2 * Environment.ProcessorCount + 1, -1, name, false)
        {
        }

        /// <summary>
        /// Minimum number of threads that should always be in pool
        /// </summary>
        public int MinThreadCount
        {
            get { return _minThreadCount; }
        }
        /// <summary>
        /// Maximum number of threads in pool
        /// </summary>
        public int MaxThreadCount
        {
            get { return _maxThreadCount; }
        }
        /// <summary>
        /// Number of threads tracked by the current DynamicThreadPool
        /// </summary>
        protected int PrimaryThreadCount
        {
            get { return GetThreadCountFromCombination(Volatile.Read(ref _dieSlotActiveFullThreadCountCombination)); }
        }
        /// <summary>
        /// Number of active threads
        /// </summary>
        public int ActiveThreadCount
        {
            get { return GetActiveThreadCountFromCombination(Volatile.Read(ref _dieSlotActiveFullThreadCountCombination)); }
        }


        /// <summary>
        /// Warms-up pool by adding new threads up to the specified '<paramref name="count"/>'
        /// </summary>
        /// <param name="count">The number of threads that must be present in the pool after the call</param>
        public void FillPoolUpTo(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            CheckPendingDisposeOrDisposed();         

            count = Math.Min(count, _maxThreadCount);

            int addCount = count - this.PrimaryThreadCount;
            while (addCount-- > 0 && this.PrimaryThreadCount < count)
                if (!this.AddNewThreadInner(count))
                    break;

            int activateCount = (count - this.ActiveThreadCount);
            while (activateCount-- > 0 && this.ActiveThreadCount < count)
                if (!AddOrActivateThread(count))
                    break;
        }


        #region ====== DieSlotActiveFullThreadCountCombination Work Proc =====

        private const int OneThreadForDieSlotActiveFullThreadCountCombination = 1;
        private const int OneActiveThreadForDieSlotActiveFullThreadCountCombination = (1 << 12);
        private const int OneDieSlotForDieSlotActiveFullThreadCountCombination = (1 << 24);

        /// <summary>
        /// Gets FullThreadCount component from DieSlotActiveFullThreadCount
        /// </summary>
        /// <param name="val">DieSlotActiveFullThreadCount value</param>
        /// <returns>Total number of threads stored in DieSlotActiveFullThreadCount</returns>
        [Pure]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetThreadCountFromCombination(int val)
        {
            return val & ((1 << 12) - 1);
        }
        /// <summary>
        /// Gets ActiveThreadCount component from DieSlotActiveFullThreadCount
        /// </summary>
        /// <param name="val">DieSlotActiveFullThreadCount value</param>
        /// <returns>Number of active threads stored in DieSlotActiveFullThreadCount</returns>
        [Pure]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetActiveThreadCountFromCombination(int val)
        {
            return (val >> 12) & ((1 << 12) - 1);
        }
        /// <summary>
        /// Gets DieSlotCount component from DieSlotActiveFullThreadCount (number of threads that should be destroyed)
        /// </summary>
        /// <param name="val">DieSlotActiveFullThreadCount value</param>
        /// <returns>Number of threads that should be destroyed</returns>
        [Pure]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetDieSlotCountFromCombination(int val)
        {
            return (val >> 24) & ((1 << 8) - 1);
        }
        /// <summary>
        /// Gets total thread count that will be in the pool after termination of all threads according to DieSlot count
        /// </summary>
        /// <param name="val">DieSlotActiveFullThreadCount value</param>
        /// <returns>Estimated alive thread count</returns>
        [Pure]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int EvaluateThreadCountFromCombination(int val)
        {
            return GetThreadCountFromCombination(val) - GetDieSlotCountFromCombination(val);
        }
        /// <summary>
        /// Gets the number of inactive (blocked) threads
        /// </summary>
        /// <param name="val">DieSlotActiveFullThreadCount value</param>
        /// <returns>Number of inactive (blocked) threads</returns>
        [Pure]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int EvaluatePausedThreadCountFromCombination(int val)
        {
            return GetThreadCountFromCombination(val) - GetActiveThreadCountFromCombination(val);
        }

        /// <summary>
        /// Gets total thread count that will be in the pool after termination of all threads according to DieSlot count
        /// </summary>
        /// <returns>Estimated alive thread count</returns>
        [Pure]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int EvaluateThreadCountFromCombination()
        {
            return EvaluateThreadCountFromCombination(Volatile.Read(ref _dieSlotActiveFullThreadCountCombination));
        }
        /// <summary>
        /// Gets the number of inactive (blocked) threads
        /// </summary>
        /// <returns>Number of inactive (blocked) threads</returns>
        [Pure]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int EvaluatePausedThreadCountFromCombination()
        {
            return EvaluatePausedThreadCountFromCombination(Volatile.Read(ref _dieSlotActiveFullThreadCountCombination));
        }

        /// <summary>
        /// Attempts to increment the number of threads stored in <see cref="_dieSlotActiveFullThreadCountCombination"/>.
        /// Verifies that the result number of threads is less than <see cref="_maxThreadCount"/>.
        /// </summary>
        /// <param name="newThreadCount">Number of threads stored in <see cref="_dieSlotActiveFullThreadCountCombination"/> after this operation completed</param>
        /// <returns>Whether the increment was succesfull (false - number of threads exceeded the <see cref="_maxThreadCount"/>)</returns>
        private bool IncrementThreadCount(out int newThreadCount)
        {
            TurboContract.Ensures(EvaluateThreadCountFromCombination() >= 0);
            TurboContract.Ensures(EvaluatePausedThreadCountFromCombination() >= 0);

            SpinWait sw = new SpinWait();
            var dieSlotActiveFullThreadCountTracker = Volatile.Read(ref _dieSlotActiveFullThreadCountCombination);
            while (GetThreadCountFromCombination(dieSlotActiveFullThreadCountTracker) < _maxThreadCount)
            {
                if (Interlocked.CompareExchange(
                        ref _dieSlotActiveFullThreadCountCombination,
                        dieSlotActiveFullThreadCountTracker + OneThreadForDieSlotActiveFullThreadCountCombination,
                        dieSlotActiveFullThreadCountTracker) == dieSlotActiveFullThreadCountTracker)
                {
                    newThreadCount = GetThreadCountFromCombination(dieSlotActiveFullThreadCountTracker + OneThreadForDieSlotActiveFullThreadCountCombination);
                    TurboContract.Assert(EvaluateThreadCountFromCombination() >= 0, conditionString: "EvaluateThreadCountFromCombination() >= 0");
                    TurboContract.Assert(EvaluatePausedThreadCountFromCombination() >= 0, conditionString: "EvaluatePausedThreadCountFromCombination() >= 0");
                    return true;
                }

                sw.SpinOnce();
                dieSlotActiveFullThreadCountTracker = Volatile.Read(ref _dieSlotActiveFullThreadCountCombination);
            }

            newThreadCount = GetThreadCountFromCombination(dieSlotActiveFullThreadCountTracker);
            return false;
        }

        /// <summary>
        /// Attempts to decrement the number of threads stored in <see cref="_dieSlotActiveFullThreadCountCombination"/>
        /// </summary>
        /// <param name="minThreadCount">Minimum thread count that always should be presented</param>
        /// <returns>Whether the decrement was succesfull</returns>
        private bool DecremenetThreadCount(int minThreadCount = 0)
        {
            TurboContract.Requires(minThreadCount >= 0, conditionString: "minThreadCount >= 0");
            TurboContract.Ensures(EvaluateThreadCountFromCombination() >= 0);
            TurboContract.Ensures(EvaluatePausedThreadCountFromCombination() >= 0);

            SpinWait sw = new SpinWait();
            var dieSlotActiveFullThreadCountTracker = Volatile.Read(ref _dieSlotActiveFullThreadCountCombination);
            while (GetThreadCountFromCombination(dieSlotActiveFullThreadCountTracker) > minThreadCount)
            {
                if (Interlocked.CompareExchange(
                        ref _dieSlotActiveFullThreadCountCombination,
                        dieSlotActiveFullThreadCountTracker - OneThreadForDieSlotActiveFullThreadCountCombination,
                        dieSlotActiveFullThreadCountTracker) == dieSlotActiveFullThreadCountTracker)
                {
                    TurboContract.Assert(EvaluateThreadCountFromCombination() >= 0, conditionString: "EvaluateThreadCountFromCombination() >= 0");
                    TurboContract.Assert(EvaluatePausedThreadCountFromCombination() >= 0, conditionString: "EvaluatePausedThreadCountFromCombination() >= 0");
                    return true;
                }

                sw.SpinOnce();
                dieSlotActiveFullThreadCountTracker = Volatile.Read(ref _dieSlotActiveFullThreadCountCombination);
            }

            return false;
        }

        /// <summary>
        /// Recalculates ActiveThreadCount, FullThreadCount and DieSlotCount for DieSlotActiveFullThreadCount when one thread is terminating
        /// </summary>
        /// <param name="val">Original value of DieSlotActiveFullThreadCount</param>
        /// <param name="wasActiveThreadCountDecremented">Whether the number of active threads was decreased</param>
        /// <returns>Recalculated value of DieSlotActiveFullThreadCount</returns>
        private int CalculateValueForDecrementThreadCountCascade(int val, out bool wasActiveThreadCountDecremented)
        {
            wasActiveThreadCountDecremented = false;
            if (GetDieSlotCountFromCombination(val) > 0)
                val -= OneDieSlotForDieSlotActiveFullThreadCountCombination;
            if (GetActiveThreadCountFromCombination(val) == GetThreadCountFromCombination(val))
            {
                wasActiveThreadCountDecremented = true;
                val -= OneActiveThreadForDieSlotActiveFullThreadCountCombination;
            }

            return val - OneThreadForDieSlotActiveFullThreadCountCombination;
        }
        /// <summary>
        /// Attempts to reduce the thread count stored in <see cref="_dieSlotActiveFullThreadCountCombination"/> also affects DieSlot and ActiveThreadCount when necessary)
        /// </summary>
        /// <param name="wasActiveThreadCountDecremented">Whether the number of active threads was decreased</param>
        /// <returns>Whether the operation was successful</returns>
        private bool DecrementThreadCountCascade(out bool wasActiveThreadCountDecremented)
        {
            TurboContract.Ensures(EvaluateThreadCountFromCombination() >= 0);
            TurboContract.Ensures(EvaluatePausedThreadCountFromCombination() >= 0);

            SpinWait sw = new SpinWait();
            var dieSlotActiveFullThreadCountTracker = Volatile.Read(ref _dieSlotActiveFullThreadCountCombination);
            while (GetThreadCountFromCombination(dieSlotActiveFullThreadCountTracker) > 0)
            {
                if (Interlocked.CompareExchange(ref _dieSlotActiveFullThreadCountCombination,
                        CalculateValueForDecrementThreadCountCascade(dieSlotActiveFullThreadCountTracker, out wasActiveThreadCountDecremented),
                        dieSlotActiveFullThreadCountTracker) == dieSlotActiveFullThreadCountTracker)
                {
                    TurboContract.Assert(EvaluateThreadCountFromCombination() >= 0, conditionString: "EvaluateThreadCountFromCombination() >= 0");
                    TurboContract.Assert(EvaluatePausedThreadCountFromCombination() >= 0, conditionString: "EvaluatePausedThreadCountFromCombination() >= 0");
                    return true;
                }

                sw.SpinOnce();
                dieSlotActiveFullThreadCountTracker = Volatile.Read(ref _dieSlotActiveFullThreadCountCombination);
            }

            wasActiveThreadCountDecremented = false;
            return false;
        }

        /// <summary>
        /// Attempts to increment active thread count stored in <see cref="_dieSlotActiveFullThreadCountCombination"/>
        /// </summary>
        /// <returns>Whether the number of active threads was incremented</returns>
        private bool IncrementActiveThreadCount()
        {
            TurboContract.Ensures(EvaluateThreadCountFromCombination() >= 0);
            TurboContract.Ensures(EvaluatePausedThreadCountFromCombination() >= 0);

            SpinWait sw = new SpinWait();
            var dieSlotActiveFullThreadCountTracker = Volatile.Read(ref _dieSlotActiveFullThreadCountCombination);
            while (GetActiveThreadCountFromCombination(dieSlotActiveFullThreadCountTracker) < GetThreadCountFromCombination(dieSlotActiveFullThreadCountTracker))
            {
                if (Interlocked.CompareExchange(ref _dieSlotActiveFullThreadCountCombination,
                        dieSlotActiveFullThreadCountTracker + OneActiveThreadForDieSlotActiveFullThreadCountCombination,
                        dieSlotActiveFullThreadCountTracker) == dieSlotActiveFullThreadCountTracker)
                {
                    TurboContract.Assert(EvaluateThreadCountFromCombination() >= 0, conditionString: "EvaluateThreadCountFromCombination() >= 0");
                    TurboContract.Assert(EvaluatePausedThreadCountFromCombination() >= 0, conditionString: "EvaluatePausedThreadCountFromCombination() >= 0");
                    return true;
                }

                sw.SpinOnce();
                dieSlotActiveFullThreadCountTracker = Volatile.Read(ref _dieSlotActiveFullThreadCountCombination);
            }

            return false;
        }

        /// <summary>
        /// Attempts to decrement the number of active thread count stored in <see cref="_dieSlotActiveFullThreadCountCombination"/>
        /// </summary>
        /// <param name="activeThreadCountLowLimit">Minimum number of active thread that should be presented in pool</param>
        /// <returns>Whether the number of active threads was decremented</returns>
        private bool DecrementActiveThreadCount(int activeThreadCountLowLimit = 0)
        {
            TurboContract.Requires(activeThreadCountLowLimit >= 0, conditionString: "activeThreadCountLowLimit >= 0");
            TurboContract.Ensures(EvaluateThreadCountFromCombination() >= 0);
            TurboContract.Ensures(EvaluatePausedThreadCountFromCombination() >= 0);

            SpinWait sw = new SpinWait();
            var dieSlotActiveFullThreadCountTracker = Volatile.Read(ref _dieSlotActiveFullThreadCountCombination);
            while (GetActiveThreadCountFromCombination(dieSlotActiveFullThreadCountTracker) > activeThreadCountLowLimit)
            {
                if (Interlocked.CompareExchange(ref _dieSlotActiveFullThreadCountCombination,
                        dieSlotActiveFullThreadCountTracker - OneActiveThreadForDieSlotActiveFullThreadCountCombination,
                        dieSlotActiveFullThreadCountTracker) == dieSlotActiveFullThreadCountTracker)
                {
                    TurboContract.Assert(EvaluateThreadCountFromCombination() >= 0, conditionString: "EvaluateThreadCountFromCombination() >= 0");
                    TurboContract.Assert(EvaluatePausedThreadCountFromCombination() >= 0, conditionString: "EvaluatePausedThreadCountFromCombination() >= 0");
                    return true;
                }

                sw.SpinOnce();
                dieSlotActiveFullThreadCountTracker = Volatile.Read(ref _dieSlotActiveFullThreadCountCombination);
            }

            return false;
        }


        /// <summary>
        /// Attempts to request the die slot (increments DieSlotCount stored in <see cref="_dieSlotActiveFullThreadCountCombination"/>)
        /// </summary>
        /// <param name="threadCountLowLimit">Minimum number of threads that should be presented in pool</param>
        /// <param name="curThreadCountMax">Upper limit to the total thread count when this operation can be performed</param>
        /// <returns>Whether the request was successful</returns>
        private bool RequestDieSlot(int threadCountLowLimit, int curThreadCountMax = int.MaxValue)
        {
            TurboContract.Requires(threadCountLowLimit >= 0, conditionString: "threadCountLowLimit >= 0");
            TurboContract.Requires(curThreadCountMax >= 0, conditionString: "curThreadCountMax >= 0");
            TurboContract.Ensures(EvaluateThreadCountFromCombination() >= 0);
            TurboContract.Ensures(EvaluatePausedThreadCountFromCombination() >= 0);

            SpinWait sw = new SpinWait();
            var dieSlotActiveFullThreadCountTracker = Volatile.Read(ref _dieSlotActiveFullThreadCountCombination);
            while (EvaluateThreadCountFromCombination(dieSlotActiveFullThreadCountTracker) > threadCountLowLimit &&
                   EvaluateThreadCountFromCombination(dieSlotActiveFullThreadCountTracker) <= curThreadCountMax &&
                   GetDieSlotCountFromCombination(dieSlotActiveFullThreadCountTracker) < 255)
            {
                if (Interlocked.CompareExchange(ref _dieSlotActiveFullThreadCountCombination,
                        dieSlotActiveFullThreadCountTracker + OneDieSlotForDieSlotActiveFullThreadCountCombination,
                        dieSlotActiveFullThreadCountTracker) == dieSlotActiveFullThreadCountTracker)
                {
                    TurboContract.Assert(EvaluateThreadCountFromCombination() >= 0, conditionString: "EvaluateThreadCountFromCombination() >= 0");
                    TurboContract.Assert(EvaluatePausedThreadCountFromCombination() >= 0, conditionString: "EvaluatePausedThreadCountFromCombination() >= 0");
                    return true;
                }

                sw.SpinOnce();
                dieSlotActiveFullThreadCountTracker = Volatile.Read(ref _dieSlotActiveFullThreadCountCombination);
            }

            return false;
        }


        #endregion



        /// <summary>
        /// Activates one of the blocked threads
        /// </summary>
        /// <returns>Whether the thread was activated</returns>
        private bool ActivateThread()
        {
            if (EvaluatePausedThreadCountFromCombination(Volatile.Read(ref _dieSlotActiveFullThreadCountCombination)) == 0)
                return false;

            bool wasThreadActivated = false;
            lock (_syncObject)
            {
                try { }
                finally
                {
                    if (IncrementActiveThreadCount())
                    {
                        _extThreadBlocker.SubstractExpectedWaiterCount(1);
                        //Console.WriteLine("Thread activated = " + ActiveThreadCount.ToString());
                        wasThreadActivated = true;
                    }
                }
            }
            return wasThreadActivated;
        }

        /// <summary>
        /// Deactivates (blocks) one of the active thread
        /// </summary>
        /// <param name="threadCountLowLimit">Mininal number of threads that should always be active</param>
        /// <returns>Whether the thread was deactivated</returns>
        private bool DeactivateThread(int threadCountLowLimit)
        {
            if (GetActiveThreadCountFromCombination(Volatile.Read(ref _dieSlotActiveFullThreadCountCombination)) <= threadCountLowLimit)
                return false;

            bool result = false;
            lock (_syncObject)
            {
                try { }
                finally
                {
                    if (DecrementActiveThreadCount(threadCountLowLimit))
                    {
                        _extThreadBlocker.AddExpectedWaiterCount(1);
                        //Console.WriteLine("Thread deactivated = " + ActiveThreadCount.ToString());
                        result = true;
                    }
                }
            }
            return result;
        }


        /// <summary>
        /// Adds a new thread to the pool and updates requried fields (should be used instead of base AddNewThread method)
        /// </summary>
        /// <param name="threadCountLimit">Limits the maximum number of threads inside ThreadPool</param>
        /// <returns>Whether the thread was created succesfully</returns>
        private bool AddNewThreadInner(int threadCountLimit)
        {
            if (State == ThreadPoolState.Stopped || (State == ThreadPoolState.StopRequested && !LetFinishedProcess) ||
                this.PrimaryThreadCount >= Math.Min(_maxThreadCount, threadCountLimit))
            {
                return false;
            }

            lock (_syncObject)
            {
                if (State == ThreadPoolState.Stopped || (State == ThreadPoolState.StopRequested && !LetFinishedProcess) ||
                    this.PrimaryThreadCount >= Math.Min(_maxThreadCount, threadCountLimit))
                {
                    return false;
                }

                //Console.WriteLine("Thread spawn = " + (this.PrimaryThreadCount + 1).ToString());

                bool result = false;
                int threadCountBefore = base.ThreadCount;
                try
                {
                    bool incrementThreadCountSuccess = IncrementThreadCount(out int trackingThreadCount);
                    TurboContract.Assert(incrementThreadCountSuccess, "Error. Thread count was not incremented");

                    result = AddNewThread(UniversalThreadProc) != null;
                }
                finally
                {
                    if (result || base.ThreadCount == threadCountBefore + 1)
                    {
                        IncrementActiveThreadCount();
                        //Console.WriteLine("Thread activated = " + this.ActiveThreadCount.ToString());
                    }
                    else
                    {
                        bool decrementThreadCountSuccess = DecremenetThreadCount();
                        TurboContract.Assert(decrementThreadCountSuccess, "Error. Thread count was not decremented");
                    }
                }

                return result;
            }
        }

        /// <summary>
        /// Activates a blocked thread if possible, otherwise adds a new thread to thread pool
        /// </summary>
        /// <param name="threadCountLimit">Limits the maximum number of threads inside ThreadPool</param>
        /// <returns>Whether the number of active threads was succesfully increased</returns>
        private bool AddOrActivateThread(int threadCountLimit)
        {
            if (ActivateThread())
                return true;

            return AddNewThreadInner(threadCountLimit);
        }



        /// <summary>
        /// Handles thread removing event and performs required action
        /// </summary>
        /// <param name="elem">Thread to be removed</param>
        protected override void OnThreadRemove(Thread elem)
        {
            TurboContract.Requires(elem != null, conditionString: "elem != null");

            base.OnThreadRemove(elem);

            lock (_syncObject)
            {
                try { }
                finally
                {
                    bool wasActiveThreadCountDecremented = false;
                    bool decrementThreadCountSuccess = DecrementThreadCountCascade(out wasActiveThreadCountDecremented);
                    TurboContract.Assert(decrementThreadCountSuccess, "Error. Thread count was not decremented.");
                    // Если не было уменьшения активных, то заблокируем лишний => уменьшае число потоков для блокирования
                    if (!wasActiveThreadCountDecremented)
                    {
                        _extThreadBlocker.SubstractExpectedWaiterCount(1);
                    }
                    else
                    {
                        //Console.WriteLine("Thread deactivated = " + this.ActiveThreadCount.ToString());
                    }
                }

                //Console.WriteLine("Thread exit: " + (this.PrimaryThreadCount + 1).ToString());
            }
        }


        /// <summary>
        /// Main processing method
        /// </summary>
        /// <param name="privateData">Thread local data</param>
        /// <param name="token">Cancellation token to stop the thread</param>
        [System.Diagnostics.DebuggerNonUserCode]
        private void UniversalThreadProc(ThreadPrivateData privateData, CancellationToken token)
        {
            if (privateData == null)
                throw new InvalidOperationException("privateData for Thread of ThreadPool can't be null");


            ThreadPoolWorkItem currentWorkItem = null;
            int lastViewedActiveThreadCount = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!_extThreadBlocker.Wait(_noWorkItemTrimPeriod, token))
                    {
                        if (RequestDieSlot(_minThreadCount))
                        {
                            //Console.WriteLine("Thread exit due to staying deactivated");
                            break;
                        }
                        // Иначе активируемся
                        ActivateThread();
                    }

                    bool itemTaken = this.TryTakeWorkItemFromQueue(privateData, out currentWorkItem, 0, new CancellationToken(), false);
                    if (itemTaken == false)
                    {
                        lastViewedActiveThreadCount = this.ActiveThreadCount;
                        // this.ActiveThreadCount <= _reasonableThreadCount - возможна гонка, но нам не критично
                        if (lastViewedActiveThreadCount <= _reasonableThreadCount)
                            itemTaken = this.TryTakeWorkItemFromQueue(privateData, out currentWorkItem, _noWorkItemTrimPeriod, token, false);
                        else
                            itemTaken = this.TryTakeWorkItemFromQueue(privateData, out currentWorkItem, NoWorkItemPreventDeactivationPeriod, token, false);
                    }

                    if (itemTaken)
                    {
                        this.RunWorkItem(currentWorkItem);
                        currentWorkItem = null;

                        _throughoutTracker.RegisterExecution();
                        if (_wasSomeProcessByThreadsFlag == false)
                            _wasSomeProcessByThreadsFlag = true;
                    }
                    else if (!token.IsCancellationRequested)
                    {
                        if (lastViewedActiveThreadCount <= _reasonableThreadCount)
                        {
                            if (this.PrimaryThreadCount > _fastSpawnThreadCountLimit)
                                DeactivateThread(_fastSpawnThreadCountLimit);
                            else
                                DeactivateThread(_minThreadCount);
                        }
                        else
                        {
                            DeactivateThread(_reasonableThreadCount);
                        }

                        //Console.WriteLine("Thread self deactivation due to empty queue");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (!token.IsCancellationRequested)
                    throw;
            }


            if (token.IsCancellationRequested)
            {
                if (LetFinishedProcess)
                {
                    while (this.TryTakeWorkItemFromQueue(privateData, out currentWorkItem))
                        this.RunWorkItem(currentWorkItem);
                }
                else
                {
                    while (this.TryTakeWorkItemFromQueue(privateData, out currentWorkItem))
                        this.CancelWorkItem(currentWorkItem);
                }
            }
        }



        /// <summary>
        /// Management function. Tracks the state of the pool and attempts to change the number of active threads or the size of the task queue.
        /// </summary>
        /// <param name="elapsedMs">Elapsed time interval from the previous call</param>
        /// <returns>True - processing completed</returns>
        private bool ManagementThreadProc(int elapsedMs)
        {
            if (State == ThreadPoolState.Stopped)
            {
                Qoollo.Turbo.Threading.ServiceStuff.ManagementThreadController.Instance.UnregisterCallback(ManagementThreadProc);
                return false;
            }


            if (elapsedMs < _managementProcessPeriod)
                return false;


            bool wasThreadSpawned = false;
            bool isCriticalCondition = false;
            bool needThreadCountAdjustment = false;
            int activeThreadCountBefore = this.ActiveThreadCount;

            // Защитная функция, когда все потоки внезапно деактивировались
            if (this.ActiveThreadCount == 0 && this.GlobalQueueWorkItemCount > 0)
                wasThreadSpawned = AddOrActivateThread(1);

            // Создаём поток в нормальном сценарии
            if (!wasThreadSpawned && this.ActiveThreadCount < _reasonableThreadCount)
            {
                if (this.GlobalQueueWorkItemCount > WorkItemPerThreadLimit * this.PrimaryThreadCount)
                    wasThreadSpawned = AddOrActivateThread(_reasonableThreadCount);
                else if (this.QueueCapacity > 0 && this.GlobalQueueWorkItemCount >= this.QueueCapacity)
                    wasThreadSpawned = AddOrActivateThread(_reasonableThreadCount);
            }

            // Пробуем расширить очередь, если прижало (проще, чем создание новых потоков)
            if (this.QueueCapacity > 0 && this.PrimaryThreadCount > 0)
                if (!_wasSomeProcessByThreadsFlag)
                    if (this.GlobalQueueWorkItemCount >= this.ExtendedQueueCapacity)
                        if (this.ExtendedQueueCapacity - this.QueueCapacity < _maxQueueCapacityExtension)
                            this.ExtendGlobalQueueCapacity(this.PrimaryThreadCount + 1);



            // Проверяем критический сценарий (много потоков просто встало)
            if (!DisableCritical)
            {
                if (!wasThreadSpawned && this.ActiveThreadCount <= _maxThreadCount && this.PrimaryThreadCount >= _reasonableThreadCount)
                {
                    if (this.GlobalQueueWorkItemCount > WorkItemPerThreadLimit * this.ActiveThreadCount || (this.QueueCapacity > 0 && this.GlobalQueueWorkItemCount >= this.QueueCapacity))
                    {
                        int totalThreadCount, runningCount, waitingCount;
                        this.ScanThreadStates(out totalThreadCount, out runningCount, out waitingCount);
                        if (runningCount <= 1 || (!_wasSomeProcessByThreadsFlag && runningCount < _reasonableThreadCount))
                        {
                            //Console.WriteLine("Critical spawn");
                            wasThreadSpawned = AddOrActivateThread(_maxThreadCount);
                            if (runningCount == 0 && _reasonableThreadCount >= 2)
                                wasThreadSpawned = AddOrActivateThread(_maxThreadCount) || wasThreadSpawned;
                            isCriticalCondition = true;
                        }
                    }
                }
            }

            // Проверяем критический сценарий (потоки работают, но возможно изменение их числа повлияет на производительность)
            if (this.MaxThreadCount > this.MinThreadCount + 1)
                if (this.ActiveThreadCount <= _maxThreadCount && this.PrimaryThreadCount >= _reasonableThreadCount)
                    if (this.GlobalQueueWorkItemCount > WorkItemPerThreadLimit * this.ActiveThreadCount || (this.QueueCapacity > 0 && this.GlobalQueueWorkItemCount >= this.QueueCapacity))
                        needThreadCountAdjustment = true;


            int threadCountChange = _throughoutTracker.RegisterAndMakeSuggestion(activeThreadCountBefore, needThreadCountAdjustment, isCriticalCondition);
            if (threadCountChange > 0)
            {
                for (int i = 0; i < threadCountChange; i++)
                    wasThreadSpawned = AddOrActivateThread(_maxThreadCount);
            }
            else if (threadCountChange < 0)
            {
                for (int i = 0; i < -threadCountChange; i++)
                    DeactivateThread(_reasonableThreadCount);
            }
            

            _wasSomeProcessByThreadsFlag = false;

            return true;
        }




        /// <summary>
        /// Adds new thread to the pool up to the <see cref="_fastSpawnThreadCountLimit"/>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryRequestNewThreadOnAdd()
        {
            int threadCount = this.ActiveThreadCount;
            if (threadCount >= _fastSpawnThreadCountLimit || threadCount >= this.GlobalQueueWorkItemCount + 2)
                return;

            this.AddOrActivateThread(_fastSpawnThreadCountLimit);
        }

        /// <summary>
        /// Places a new task to the thread pool queue
        /// </summary>
        /// <param name="item">Thread pool work item</param>
        protected sealed override void AddWorkItem(ThreadPoolWorkItem item)
        {
            TurboContract.Requires(item != null, conditionString: "item != null");

            CheckDisposed();
            if (IsAddingCompleted)
                throw new InvalidOperationException("Adding was completed for ThreadPool: " + Name);

            this.PrepareWorkItem(item);
            this.AddWorkItemToQueue(item);

            TryRequestNewThreadOnAdd();
        }
        /// <summary>
        /// Attemts to place a new task to the thread pool queue
        /// </summary>
        /// <param name="item">Thread pool work item</param>
        /// <returns>True if work item was added to the queue, otherwise false</returns>
        protected sealed override bool TryAddWorkItem(ThreadPoolWorkItem item)
        {
            TurboContract.Requires(item != null, conditionString: "item != null");

            if (State == ThreadPoolState.Stopped || IsAddingCompleted)
                return false;

            bool result = false;

            if (QueueCapacity <= 0 || GlobalQueueWorkItemCount < QueueCapacity)
            {
                this.PrepareWorkItem(item);
                result = this.TryAddWorkItemToQueue(item);
            }

            TryRequestNewThreadOnAdd();
            return result;
        }


#if DEBUG
        /// <summary>
        /// Finalizer
        /// </summary>
        ~DynamicThreadPool()
        {
            Dispose(false);
        }
#endif
    }
}
