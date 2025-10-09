#region Release History

// Smart Thread Pool
// 7 Aug 2004 - Initial release
//
// 14 Sep 2004 - Bug fixes 
//
// 15 Oct 2004 - Added new features
//		- Work items return result.
//		- Support waiting synchronization for multiple work items.
//		- Work items can be cancelled.
//		- Passage of the caller thread?s context to the thread in the pool.
//		- Minimal usage of WIN32 handles.
//		- Minor bug fixes.
//
// 26 Dec 2004 - Changes:
//		- Removed static constructors.
//      - Added finalizers.
//		- Changed Exceptions so they are serializable.
//		- Fixed the bug in one of the SmartThreadPool constructors.
//		- Changed the SmartThreadPool.WaitAll() so it will support any number of waiters. 
//        The SmartThreadPool.WaitAny() is still limited by the .NET Framework.
//		- Added PostExecute with options on which cases to call it.
//      - Added option to dispose of the state objects.
//      - Added a WaitForIdle() method that waits until the work items queue is empty.
//      - Added an STPStartInfo class for the initialization of the thread pool.
//      - Changed exception handling so if a work item throws an exception it 
//        is rethrown at GetResult(), rather then firing an UnhandledException event.
//        Note that PostExecute exception are always ignored.
//
// 25 Mar 2005 - Changes:
//		- Fixed lost of work items bug
//
// 3 Jul 2005: Changes.
//      - Fixed bug where Enqueue() throws an exception because PopWaiter() returned null, hardly reconstructed. 
//
// 16 Aug 2005: Changes.
//		- Fixed bug where the InUseThreads becomes negative when canceling work items. 
//
// 31 Jan 2006 - Changes:
//		- Added work items priority
//		- Removed support of chained delegates in callbacks and post executes (nobody really use this)
//		- Added work items groups
//		- Added work items groups idle event
//		- Changed SmartThreadPool.WaitAll() behavior so when it gets empty array
//		  it returns true rather then throwing an exception.
//		- Added option to start the STP and the WIG as suspended
//		- Exception behavior changed, the real exception is returned by an 
//		  inner exception
//		- Added option to keep the Http context of the caller thread. (Thanks to Steven T.)
//		- Added performance counters
//		- Added priority to the threads in the pool
//
// 13 Feb 2006 - Changes:
//		- Added a call to the dispose of the Performance Counter so
//		  their won't be a Performance Counter leak.
//		- Added exception catch in case the Performance Counters cannot 
//		  be created.
//
// 17 May 2008 - Changes:
//      - Changed the dispose behavior and removed the Finalizers.
//      - Enabled the change of the MaxThreads and MinThreads at run time.
//      - Enabled the change of the Concurrency of a IWorkItemsGroup at run 
//        time If the IWorkItemsGroup is a SmartThreadPool then the Concurrency 
//        refers to the MaxThreads. 
//      - Improved the cancel behavior.
//      - Added events for thread creation and termination. 
//      - Fixed the HttpContext context capture.
//      - Changed internal collections so they use generic collections
//      - Added IsIdle flag to the SmartThreadPool and IWorkItemsGroup
//      - Added support for WinCE
//      - Added support for Action<T> and Func<T>
//
// 07 April 2009 - Changes:
//      - Added support for Silverlight and Mono
//      - Added Join, Choice, and Pipe to SmartThreadPool.
//      - Added local performance counters (for Mono, Silverlight, and WindowsCE)
//      - Changed duration measures from DateTime.Now to Stopwatch.
//      - Queues changed from System.Collections.Queue to System.Collections.Generic.LinkedList<T>.
//
// 21 December 2009 - Changes:
//      - Added work item timeout (passive)
//
// 20 August 2012 - Changes:
//      - Added set name to threads
//      - Fixed the WorkItemsQueue.Dequeue. 
//        Replaced while (!Monitor.TryEnter(this)); with lock(this) { ... }
//      - Fixed SmartThreadPool.Pipe
//      - Added IsBackground option to threads
//      - Added ApartmentState to threads
//      - Fixed thread creation when queuing many work items at the same time.
//
// 24 August 2012 - Changes:
//      - Enabled cancel abort after cancel. See: http://smartthreadpool.codeplex.com/discussions/345937 by alecswan
//      - Added option to set MaxStackSize of threads 
//
// 16 September 2016 - Changes:
//      - Separated the STP project to .NET 2.0, .NET 4.0, and .NET 4.5
//      - Added option to set MaxQueueLength (Thanks to Rob Hruska)
//
// 31 May 2019 - Changes:
//      - Added .Net Standard 2.0 support
//
// 24 Feb 2020 - Changes:
//		- Added .Net Core 3.x support
//			* Removed the use of Thread.Abort(). The Shutdown method doesn't get forceAbort argument in the .net core versions
//			* Fixed #if for .net core, .net standard, and .net framework.
//			* Enabled tests to run on .net core too
//			* Fixed/Removed tests which depend on Thread.Abort.
//
// 16 July 2021 - Changes:
//		- Added .Net Core 5.0 support

#endregion

using System;
using System.Security;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices; // for RuntimeInformation

using Amib.Threading.Internal;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Amib.Threading
{
    #region SmartThreadPool class
    /// <summary>
    /// Smart thread pool class.
    /// </summary>
    public partial class SmartThreadPool : WorkItemsGroupBase, IDisposable
    {
        #region Public Default Constants

        public const int DefaultMinWorkerThreads = 0;
        public const int DefaultMaxWorkerThreads = 25;
        public const int DefaultIdleTimeout = 60 * 1000; // One minute
        public const bool DefaultUseCallerCallContext = false;
        public const bool DefaultUseCallerHttpContext = false;
        public const bool DefaultDisposeOfStateObjects = false;
        public const CallToPostExecute DefaultCallToPostExecute = CallToPostExecute.Always;
        public static readonly PostExecuteWorkItemCallback DefaultPostExecuteWorkItemCallback;
        public const WorkItemPriority DefaultWorkItemPriority = WorkItemPriority.Normal;
        public const bool DefaultStartSuspended = false;
        public static readonly string DefaultPerformanceCounterInstanceName;
        public const ThreadPriority DefaultThreadPriority = ThreadPriority.Normal;
        public const string DefaultThreadPoolName = "SmartThreadPool";
        public static readonly int? DefaultMaxStackSize = null;
        public static readonly int? DefaultMaxQueueLength = null;
        public const bool DefaultFillStateWithArgs = false;
        public const bool DefaultAreThreadsBackground = true;
        public const ApartmentState DefaultApartmentState = ApartmentState.Unknown;

        #endregion

        #region Member Variables

        private readonly SynchronizedDictionary<Thread, ThreadEntry> _workerThreads = new SynchronizedDictionary<Thread, ThreadEntry>();
        private readonly WorkItemsQueue _workItemsQueue = new WorkItemsQueue();
        private int _workItemsProcessed;
        private int _inUseWorkerThreads;
        private STPStartInfo _stpStartInfo;
        private volatile int _currentWorkItemsCount;
        private ManualResetEvent _isIdleWaitHandle = EventWaitHandleFactory.CreateManualResetEvent(true);
        private ManualResetEvent _shuttingDownEvent = EventWaitHandleFactory.CreateManualResetEvent(false);
        private bool _isSuspended;
        private bool _shutdown;
        private int _threadCounter;
        private bool _isDisposed;
        private readonly SynchronizedDictionary<IWorkItemsGroup, IWorkItemsGroup> _workItemsGroups = new SynchronizedDictionary<IWorkItemsGroup, IWorkItemsGroup>();
        private CanceledWorkItemsGroup _canceledSmartThreadPool = new CanceledWorkItemsGroup();
        private ISTPInstancePerformanceCounters _windowsPCs = NullSTPInstancePerformanceCounters.Instance;
        private ISTPInstancePerformanceCounters _localPCs = NullSTPInstancePerformanceCounters.Instance;

        [ThreadStatic]
        private static ThreadEntry _threadEntry;

        private event ThreadInitializationHandler _onThreadInitialization;
        private event ThreadTerminationHandler _onThreadTermination;

        #endregion

        #region Per thread properties

        internal static ThreadEntry CurrentThreadEntry
        {
            get { return _threadEntry; }
            set { _threadEntry = value; }
        }

        #endregion

        #region Construction and Finalization

        public SmartThreadPool()
        {
            _stpStartInfo = new STPStartInfo();
            Initialize();
        }

        public SmartThreadPool(int idleTimeout)
        {
            _stpStartInfo = new STPStartInfo { IdleTimeout = idleTimeout };
            Initialize();
        }

        public SmartThreadPool(bool startSuspended)
        {
            _stpStartInfo = new STPStartInfo { StartSuspended = startSuspended };
            Initialize();
        }

        public SmartThreadPool(int idleTimeout, int maxWorkerThreads)
        {
            _stpStartInfo = new STPStartInfo
            {
                IdleTimeout = idleTimeout,
                MaxWorkerThreads = maxWorkerThreads,
            };
            Initialize();
        }

        public SmartThreadPool(int idleTimeout, int maxWorkerThreads, int minWorkerThreads)
        {
            _stpStartInfo = new STPStartInfo
            {
                IdleTimeout = idleTimeout,
                MaxWorkerThreads = maxWorkerThreads,
                MinWorkerThreads = minWorkerThreads,
            };
            Initialize();
        }

        public SmartThreadPool(STPStartInfo stpStartInfo)
        {
            _stpStartInfo = new STPStartInfo(stpStartInfo);
            Initialize();
        }

        private void Initialize()
        {
            Name = _stpStartInfo.ThreadPoolName;
            ValidateSTPStartInfo();

            _isSuspended = _stpStartInfo.StartSuspended;

#if !(NETFRAMEWORK)
            if (null != _stpStartInfo.PerformanceCounterInstanceName)
			{
                throw new NotSupportedException("Performance counters are not implemented for Compact Framework/Silverlight/Mono, instead use StpStartInfo.EnableLocalPerformanceCounters");
            }
#else
            if (null != _stpStartInfo.PerformanceCounterInstanceName)
            {
                try
                {
                    _windowsPCs = new STPInstancePerformanceCounters(_stpStartInfo.PerformanceCounterInstanceName);
                }
                catch (Exception e)
                {
                    Debug.WriteLine("Unable to create Performance Counters: " + e);
                    _windowsPCs = NullSTPInstancePerformanceCounters.Instance;
                }
            }
#endif

            if (_stpStartInfo.EnableLocalPerformanceCounters)
            {
                _localPCs = new LocalSTPInstancePerformanceCounters();
            }

            if (!_isSuspended)
            {
                StartOptimalNumberOfThreads();
            }
        }

        private void StartOptimalNumberOfThreads()
        {
            int threadsCount = Math.Max(_workItemsQueue.Count, _stpStartInfo.MinWorkerThreads);
            threadsCount = Math.Min(threadsCount, _stpStartInfo.MaxWorkerThreads);
            threadsCount -= _workerThreads.Count;
            if (threadsCount > 0)
            {
                StartThreads(threadsCount);
            }
        }

        private void ValidateSTPStartInfo()
        {
            if (_stpStartInfo.MinWorkerThreads < 0)
            {
                throw new ArgumentOutOfRangeException("MinWorkerThreads", "MinWorkerThreads cannot be negative");
            }

            if (_stpStartInfo.MaxWorkerThreads <= 0)
            {
                throw new ArgumentOutOfRangeException("MaxWorkerThreads", "MaxWorkerThreads must be greater than zero");
            }

            if (_stpStartInfo.MinWorkerThreads > _stpStartInfo.MaxWorkerThreads)
            {
                throw new ArgumentOutOfRangeException("MinWorkerThreads, maxWorkerThreads", "MaxWorkerThreads must be greater or equal to MinWorkerThreads");
            }

            if (_stpStartInfo.MaxQueueLength < 0)
            {
                throw new ArgumentOutOfRangeException("MaxQueueLength", "MaxQueueLength must be >= 0 or null (for unbounded)");
            }
        }

        private static void ValidateCallback(Delegate callback)
        {
            if (callback.GetInvocationList().Length > 1)
            {
                throw new NotSupportedException("SmartThreadPool doesn't support delegates chains");
            }
        }

        #endregion

        #region Thread Processing

        private WorkItem Dequeue()
        {
            WorkItem workItem = _workItemsQueue.DequeueWorkItem(_stpStartInfo.IdleTimeout, _shuttingDownEvent);
            return workItem;
        }

        internal override void Enqueue(WorkItem workItem)
        {
            Debug.Assert(null != workItem);

            IncrementWorkItemsCount();

            workItem.CanceledSmartThreadPool = _canceledSmartThreadPool;
            _workItemsQueue.EnqueueWorkItem(workItem);
            workItem.WorkItemIsQueued();

            if (_currentWorkItemsCount > _workerThreads.Count)
            {
                StartThreads(1);
            }
        }

        private void IncrementWorkItemsCount()
        {
            _windowsPCs.SampleWorkItems(_workItemsQueue.Count, _workItemsProcessed);
            _localPCs.SampleWorkItems(_workItemsQueue.Count, _workItemsProcessed);

            int count = Interlocked.Increment(ref _currentWorkItemsCount);
            if (count == 1)
            {
                IsIdle = false;
                _isIdleWaitHandle.Reset();
            }
        }

        private void DecrementWorkItemsCount()
        {
            int count = Interlocked.Decrement(ref _currentWorkItemsCount);
            if (count == 0)
            {
                IsIdle = true;
                _isIdleWaitHandle.Set();
            }

            Interlocked.Increment(ref _workItemsProcessed);

            if (!_shutdown)
            {
                _windowsPCs.SampleWorkItems(_workItemsQueue.Count, _workItemsProcessed);
                _localPCs.SampleWorkItems(_workItemsQueue.Count, _workItemsProcessed);
            }
        }

        internal void RegisterWorkItemsGroup(IWorkItemsGroup workItemsGroup)
        {
            _workItemsGroups[workItemsGroup] = workItemsGroup;
        }

        internal void UnregisterWorkItemsGroup(IWorkItemsGroup workItemsGroup)
        {
            if (_workItemsGroups.Contains(workItemsGroup))
            {
                _workItemsGroups.Remove(workItemsGroup);
            }
        }

        private void InformCompleted()
        {
            if (_workerThreads.Contains(Thread.CurrentThread))
            {
                _workerThreads.Remove(Thread.CurrentThread);
                _windowsPCs.SampleThreads(_workerThreads.Count, _inUseWorkerThreads);
                _localPCs.SampleThreads(_workerThreads.Count, _inUseWorkerThreads);
            }
        }

        private void StartThreads(int threadsCount)
        {
            if (_isSuspended) return;

            lock (_workerThreads.SyncRoot)
            {
                if (_shutdown) return;

                for (int i = 0; i < threadsCount; ++i)
                {
                    if (_workerThreads.Count >= _stpStartInfo.MaxWorkerThreads) return;

                    Thread workerThread =
                        _stpStartInfo.MaxStackSize.HasValue
                        ? new Thread(ProcessQueuedItems, _stpStartInfo.MaxStackSize.Value)
                        : new Thread(ProcessQueuedItems);

                    workerThread.Name = "STP " + Name + " Thread #" + _threadCounter;
                    workerThread.IsBackground = _stpStartInfo.AreThreadsBackground;

                    // Guard SetApartmentState by platform
#if NET6_0_OR_GREATER
                    if (OperatingSystem.IsWindows() && _stpStartInfo.ApartmentState != ApartmentState.Unknown)
                    {
                        try { workerThread.SetApartmentState(_stpStartInfo.ApartmentState); } catch { }
                    }
#elif NETCOREAPP || NETSTANDARD || NETFRAMEWORK
                    if (_stpStartInfo.ApartmentState != ApartmentState.Unknown)
                    {
                        try
                        {
                            // On non-Windows some runtimes throw; ignore
#if NETCOREAPP || NETSTANDARD
                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                workerThread.SetApartmentState(_stpStartInfo.ApartmentState);
#else
                            workerThread.SetApartmentState(_stpStartInfo.ApartmentState);
#endif
                        }
                        catch { }
                    }
#endif

                    workerThread.Priority = _stpStartInfo.ThreadPriority;
                    workerThread.Start();
                    ++_threadCounter;

                    _workerThreads[workerThread] = new ThreadEntry(this);

                    _windowsPCs.SampleThreads(_workerThreads.Count, _inUseWorkerThreads);
                    _localPCs.SampleThreads(_workerThreads.Count, _inUseWorkerThreads);
                }
            }
        }

        private void ProcessQueuedItems()
        {
            CurrentThreadEntry = _workerThreads[Thread.CurrentThread];

            FireOnThreadInitialization();

            try
            {
                bool bInUseWorkerThreadsWasIncremented = false;

                while (!_shutdown)
                {
                    CurrentThreadEntry.IAmAlive();

                    if (_workerThreads.Count > _stpStartInfo.MaxWorkerThreads)
                    {
                        lock (_workerThreads.SyncRoot)
                        {
                            if (_workerThreads.Count > _stpStartInfo.MaxWorkerThreads)
                            {
                                InformCompleted();
                                break;
                            }
                        }
                    }

                    WorkItem workItem = Dequeue();

                    CurrentThreadEntry.IAmAlive();

                    if (null == workItem)
                    {
                        if (_workerThreads.Count > _stpStartInfo.MinWorkerThreads)
                        {
                            lock (_workerThreads.SyncRoot)
                            {
                                if (_workerThreads.Count > _stpStartInfo.MinWorkerThreads)
                                {
                                    InformCompleted();
                                    break;
                                }
                            }
                        }
                    }

                    if (null == workItem) continue;

                    try
                    {
                        bInUseWorkerThreadsWasIncremented = false;

                        CurrentThreadEntry.CurrentWorkItem = workItem;

                        if (!workItem.StartingWorkItem())
                        {
                            continue;
                        }

                        int inUseWorkerThreads = Interlocked.Increment(ref _inUseWorkerThreads);
                        _windowsPCs.SampleThreads(_workerThreads.Count, inUseWorkerThreads);
                        _localPCs.SampleThreads(_workerThreads.Count, inUseWorkerThreads);

                        bInUseWorkerThreadsWasIncremented = true;

                        workItem.FireWorkItemStarted();

                        ExecuteWorkItem(workItem);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cooperative cancellation: ignore
                    }
#if NETFRAMEWORK
                    catch (ThreadAbortException)
                    {
                        try { Thread.ResetAbort(); } catch { }
                    }
#endif
                    catch (Exception ex)
                    {
                        ex.GetHashCode(); // keep legacy swallow
                    }
                    finally
                    {
                        workItem.DisposeOfState();

                        CurrentThreadEntry.CurrentWorkItem = null;

                        if (bInUseWorkerThreadsWasIncremented)
                        {
                            int inUseWorkerThreads = Interlocked.Decrement(ref _inUseWorkerThreads);
                            _windowsPCs.SampleThreads(_workerThreads.Count, inUseWorkerThreads);
                            _localPCs.SampleThreads(_workerThreads.Count, _inUseWorkerThreads);
                        }

                        workItem.FireWorkItemCompleted();

                        DecrementWorkItemsCount();
                    }
                }
            }
            catch (Exception e)
            {
                e.GetHashCode();
            }
            finally
            {
                InformCompleted();
                FireOnThreadTermination();
            }
        }

        private void ExecuteWorkItem(WorkItem workItem)
        {
            _windowsPCs.SampleWorkItemsWaitTime(workItem.WaitingTime);
            _localPCs.SampleWorkItemsWaitTime(workItem.WaitingTime);
            try
            {
                workItem.Execute();
            }
            finally
            {
                _windowsPCs.SampleWorkItemsProcessTime(workItem.ProcessTime);
                _localPCs.SampleWorkItemsProcessTime(workItem.ProcessTime);
            }
        }

        #endregion

        #region Public Methods

        private void ValidateWaitForIdle()
        {
            if (null != CurrentThreadEntry && CurrentThreadEntry.AssociatedSmartThreadPool == this)
            {
                throw new NotSupportedException("WaitForIdle cannot be called from a thread on its SmartThreadPool, it causes a deadlock");
            }
        }

        internal static void ValidateWorkItemsGroupWaitForIdle(IWorkItemsGroup workItemsGroup)
        {
            if (null == CurrentThreadEntry) return;

            WorkItem workItem = CurrentThreadEntry.CurrentWorkItem;
            ValidateWorkItemsGroupWaitForIdleImpl(workItemsGroup, workItem);
            if ((null != workItemsGroup) &&
                (null != workItem) &&
                CurrentThreadEntry.CurrentWorkItem.WasQueuedBy(workItemsGroup))
            {
                throw new NotSupportedException("WaitForIdle cannot be called from a thread on its SmartThreadPool, it causes a deadlock");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ValidateWorkItemsGroupWaitForIdleImpl(IWorkItemsGroup workItemsGroup, WorkItem workItem)
        {
            if ((null != workItemsGroup) &&
                (null != workItem) &&
                workItem.WasQueuedBy(workItemsGroup))
            {
                throw new NotSupportedException("WaitForIdle cannot be called from a thread on its SmartThreadPool, it causes a deadlock");
            }
        }

        /// <summary>
        /// Force the SmartThreadPool to shutdown (cooperative).
        /// </summary>
        public void Shutdown()
        {
            ShutdownImpl(false, 0);
        }

        /// <summary>
        /// Force the SmartThreadPool to shutdown with timeout (cooperative).
        /// </summary>
        public void Shutdown(TimeSpan timeout)
        {
            ShutdownImpl(false, (int)timeout.TotalMilliseconds);
        }

        /// <summary>
        /// Empties the queue of work items and shuts down with timeout (cooperative).
        /// </summary>
        public void Shutdown(int millisecondsTimeout)
        {
            ShutdownImpl(false, millisecondsTimeout);
        }

#if !(NETCOREAPP)
        public void Shutdown(bool forceAbort)
        {
            ShutdownImpl(forceAbort, 0);
        }

        public void Shutdown(bool forceAbort, TimeSpan timeout)
        {
            ShutdownImpl(forceAbort, (int)timeout.TotalMilliseconds);
        }

        public void Shutdown(bool forceAbort, int millisecondsTimeout)
        {
            ShutdownImpl(forceAbort, millisecondsTimeout);
        }
#endif

        private void ShutdownImpl(bool forceAbort, int millisecondsTimeout)
        {
            ValidateNotDisposed();

            ISTPInstancePerformanceCounters pcs = _windowsPCs;

            if (NullSTPInstancePerformanceCounters.Instance != _windowsPCs)
            {
                _windowsPCs = NullSTPInstancePerformanceCounters.Instance;
                pcs.Dispose();
            }

            Thread[] threads;
            lock (_workerThreads.SyncRoot)
            {
                _workItemsQueue.Dispose();

                _shutdown = true;
                _shuttingDownEvent.Set();

                threads = new Thread[_workerThreads.Count];
                _workerThreads.Keys.CopyTo(threads, 0);
            }

            int millisecondsLeft = millisecondsTimeout;
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool waitInfinitely = (Timeout.Infinite == millisecondsTimeout);
            bool timedOut = false;

            foreach (Thread thread in threads)
            {
                if (!waitInfinitely && (millisecondsLeft < 0))
                {
                    timedOut = true;
                    break;
                }

                bool success = thread.Join(millisecondsLeft);
                if (!success)
                {
                    timedOut = true;
                    break;
                }

                if (!waitInfinitely)
                {
                    millisecondsLeft = millisecondsTimeout - (int)stopwatch.ElapsedMilliseconds;
                }
            }

#if NETFRAMEWORK
            // Legacy: Only .NET Framework supports Thread.Abort
            if (timedOut && forceAbort)
            {
                foreach (Thread thread in threads)
                {
                    if (thread != null && thread.IsAlive)
                    {
                        try { thread.Abort(); }
                        catch (SecurityException) { }
                        catch (ThreadStateException) { }
                    }
                }
            }
#else
    // Modern .NET: ignore forceAbort; cooperative shutdown only.
    if (timedOut)
    {
        // Log or optionally trace the timeout.
        Debug.WriteLine("[SmartThreadPool] Shutdown timed out; some threads may still be finishing.");
    }
#endif
        }

        public static bool WaitAll(IWaitableResult[] waitableResults)
        {
            return WaitAll(waitableResults, Timeout.Infinite, true);
        }

        public static bool WaitAll(IWaitableResult[] waitableResults, TimeSpan timeout, bool exitContext)
        {
            return WaitAll(waitableResults, (int)timeout.TotalMilliseconds, exitContext);
        }

        public static bool WaitAll(IWaitableResult[] waitableResults, int millisecondsTimeout, bool exitContext)
        {
            return WorkItem.WaitAll(waitableResults, millisecondsTimeout, exitContext, null);
        }

        public static bool WaitAll(IWaitableResult[] waitableResults, TimeSpan timeout, bool exitContext, WaitHandle cancelWaitHandle)
        {
            return WaitAll(waitableResults, (int)timeout.TotalMilliseconds, exitContext, cancelWaitHandle);
        }

        public static bool WaitAll(IWaitableResult[] waitableResults, int millisecondsTimeout, bool exitContext, WaitHandle cancelWaitHandle)
        {
            return WorkItem.WaitAll(waitableResults, millisecondsTimeout, exitContext, cancelWaitHandle);
        }

        public static int WaitAny(IWaitableResult[] waitableResults)
        {
            return WaitAny(waitableResults, Timeout.Infinite, true);
        }

        public static int WaitAny(IWaitableResult[] waitableResults, TimeSpan timeout, bool exitContext)
        {
            return WaitAny(waitableResults, (int)timeout.TotalMilliseconds, exitContext);
        }

        public static int WaitAny(IWaitableResult[] waitableResults, int millisecondsTimeout, bool exitContext)
        {
            return WorkItem.WaitAny(waitableResults, millisecondsTimeout, exitContext, null);
        }

        public static int WaitAny(IWaitableResult[] waitableResults, TimeSpan timeout, bool exitContext, WaitHandle cancelWaitHandle)
        {
            return WaitAny(waitableResults, (int)timeout.TotalMilliseconds, exitContext, cancelWaitHandle);
        }

        public static int WaitAny(IWaitableResult[] waitableResults, int millisecondsTimeout, bool exitContext, WaitHandle cancelWaitHandle)
        {
            return WorkItem.WaitAny(waitableResults, millisecondsTimeout, exitContext, cancelWaitHandle);
        }

        public IWorkItemsGroup CreateWorkItemsGroup(int concurrency)
        {
            IWorkItemsGroup workItemsGroup = new WorkItemsGroup(this, concurrency, _stpStartInfo);
            return workItemsGroup;
        }

        public IWorkItemsGroup CreateWorkItemsGroup(int concurrency, WIGStartInfo wigStartInfo)
        {
            IWorkItemsGroup workItemsGroup = new WorkItemsGroup(this, concurrency, wigStartInfo);
            return workItemsGroup;
        }

        #region Fire Thread's Events

        private void FireOnThreadInitialization()
        {
            if (null != _onThreadInitialization)
            {
                foreach (ThreadInitializationHandler tih in _onThreadInitialization.GetInvocationList())
                {
                    try { tih(); }
                    catch (Exception e) { e.GetHashCode(); Debug.Assert(false); throw; }
                }
            }
        }

        private void FireOnThreadTermination()
        {
            if (null != _onThreadTermination)
            {
                foreach (ThreadTerminationHandler tth in _onThreadTermination.GetInvocationList())
                {
                    try { tth(); }
                    catch (Exception e) { e.GetHashCode(); Debug.Assert(false); throw; }
                }
            }
        }

        #endregion

        public event ThreadInitializationHandler OnThreadInitialization
        {
            add { _onThreadInitialization += value; }
            remove { _onThreadInitialization -= value; }
        }

        public event ThreadTerminationHandler OnThreadTermination
        {
            add { _onThreadTermination += value; }
            remove { _onThreadTermination -= value; }
        }

        internal void CancelAbortWorkItemsGroup(WorkItemsGroup wig)
        {
            foreach (ThreadEntry threadEntry in _workerThreads.Values)
            {
                WorkItem workItem = threadEntry.CurrentWorkItem;
                if (null != workItem &&
                    workItem.WasQueuedBy(wig) &&
                    !workItem.IsCanceled)
                {
                    threadEntry.CurrentWorkItem.GetWorkItemResult().Cancel(true);
                }
            }
        }

        private void ValidateQueueIsWithinLimits()
        {
            var maxQueueLength = _stpStartInfo.MaxQueueLength;
            if (maxQueueLength == null) return;

            if (_currentWorkItemsCount >= maxQueueLength + MaxThreads)
            {
                throw new QueueRejectedException("Queue is at its maximum (" + maxQueueLength + ")");
            }
        }

        #endregion

        #region Properties

        public int MinThreads
        {
            get { ValidateNotDisposed(); return _stpStartInfo.MinWorkerThreads; }
            set
            {
                Debug.Assert(value >= 0);
                Debug.Assert(value <= _stpStartInfo.MaxWorkerThreads);
                if (_stpStartInfo.MaxWorkerThreads < value) _stpStartInfo.MaxWorkerThreads = value;
                _stpStartInfo.MinWorkerThreads = value;
                StartOptimalNumberOfThreads();
            }
        }

        public int MaxThreads
        {
            get { ValidateNotDisposed(); return _stpStartInfo.MaxWorkerThreads; }
            set
            {
                Debug.Assert(value > 0);
                Debug.Assert(value >= _stpStartInfo.MinWorkerThreads);
                if (_stpStartInfo.MinWorkerThreads > value) _stpStartInfo.MinWorkerThreads = value;
                _stpStartInfo.MaxWorkerThreads = value;
                StartOptimalNumberOfThreads();
            }
        }

        public int? MaxQueueLength
        {
            get { ValidateNotDisposed(); return _stpStartInfo.MaxQueueLength; }
            set { _stpStartInfo.MaxQueueLength = value; }
        }

        public int ActiveThreads
        {
            get { ValidateNotDisposed(); return _workerThreads.Count; }
        }

        public int CurrentWorkItemsCount
        {
            get { ValidateNotDisposed(); return _currentWorkItemsCount; }
        }

        public static bool IsWorkItemCanceled
        {
            get { return CurrentThreadEntry.CurrentWorkItem.IsCanceled; }
        }

        /// <summary>
        /// Cooperative cancellation: throw OperationCanceledException instead of Thread.Abort.
        /// </summary>
        public static void AbortOnWorkItemCancel()
        {
            if (IsWorkItemCanceled)
            {
                throw new OperationCanceledException("Work item was canceled.");
            }
        }

        public STPStartInfo STPStartInfo
        {
            get { return _stpStartInfo.AsReadOnly(); }
        }

        public bool IsShuttingdown
        {
            get { return _shutdown; }
        }

        public ISTPPerformanceCountersReader PerformanceCountersReader
        {
            get { return (ISTPPerformanceCountersReader)_localPCs; }
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            if (!_isDisposed)
            {
                if (!_shutdown)
                {
                    Shutdown();
                }

                if (null != _shuttingDownEvent)
                {
                    _shuttingDownEvent.Close();
                    _shuttingDownEvent = null;
                }
                _workerThreads.Clear();

                if (null != _isIdleWaitHandle)
                {
                    _isIdleWaitHandle.Close();
                    _isIdleWaitHandle = null;
                }

                _isDisposed = true;
            }
        }

        private void ValidateNotDisposed()
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(GetType().ToString(), "The SmartThreadPool has been shutdown");
            }
        }
        #endregion

        #region WorkItemsGroupBase Overrides

        public override int Concurrency
        {
            get { return MaxThreads; }
            set { MaxThreads = value; }
        }

        public override int InUseThreads
        {
            get { ValidateNotDisposed(); return _inUseWorkerThreads; }
        }

        public override int WaitingCallbacks
        {
            get { ValidateNotDisposed(); return _workItemsQueue.Count; }
        }

        public override object[] GetStates()
        {
            object[] states = _workItemsQueue.GetStates();
            return states;
        }

        public override WIGStartInfo WIGStartInfo
        {
            get { return _stpStartInfo.AsReadOnly(); }
        }

        public override void Start()
        {
            if (!_isSuspended) return;
            _isSuspended = false;

            ICollection workItemsGroups = _workItemsGroups.Values;
            foreach (WorkItemsGroup workItemsGroup in workItemsGroups)
            {
                workItemsGroup.OnSTPIsStarting();
            }

            StartOptimalNumberOfThreads();
        }

        public override void Cancel(bool abortExecution)
        {
            _canceledSmartThreadPool.IsCanceled = true;
            _canceledSmartThreadPool = new CanceledWorkItemsGroup();

            ICollection workItemsGroups = _workItemsGroups.Values;
            foreach (WorkItemsGroup workItemsGroup in workItemsGroups)
            {
                workItemsGroup.Cancel(abortExecution);
            }

            if (abortExecution)
            {
                foreach (ThreadEntry threadEntry in _workerThreads.Values)
                {
                    WorkItem workItem = threadEntry.CurrentWorkItem;
                    if (null != workItem &&
                        threadEntry.AssociatedSmartThreadPool == this &&
                        !workItem.IsCanceled)
                    {
                        threadEntry.CurrentWorkItem.GetWorkItemResult().Cancel(true);
                    }
                }
            }
        }

        public override bool WaitForIdle(int millisecondsTimeout)
        {
            ValidateWaitForIdle();
            return STPEventWaitHandle.WaitOne(_isIdleWaitHandle, millisecondsTimeout, false);
        }

        public override event WorkItemsGroupIdleHandler OnIdle
        {
            add
            {
                throw new NotImplementedException("This event is not implemented in the SmartThreadPool class. Please create a WorkItemsGroup in order to use this feature.");
            }
            remove
            {
                throw new NotImplementedException("This event is not implemented in the SmartThreadPool class. Please create a WorkItemsGroup in order to use this feature.");
            }
        }

        internal override void PreQueueWorkItem()
        {
            ValidateNotDisposed();
            ValidateQueueIsWithinLimits();
        }

        #endregion

        #region Join, Choice, Pipe, etc.

        public void Join(IEnumerable<Action> actions)
        {
            WIGStartInfo wigStartInfo = new WIGStartInfo { StartSuspended = true };
            IWorkItemsGroup workItemsGroup = CreateWorkItemsGroup(int.MaxValue, wigStartInfo);
            foreach (Action action in actions)
            {
                workItemsGroup.QueueWorkItem(action);
            }
            workItemsGroup.Start();
            workItemsGroup.WaitForIdle();
        }

        public void Join(params Action[] actions)
        {
            Join((IEnumerable<Action>)actions);
        }

        private class ChoiceIndex
        {
            public int _index = -1;
        }

        public int Choice(IEnumerable<Action> actions)
        {
            WIGStartInfo wigStartInfo = new WIGStartInfo { StartSuspended = true };
            IWorkItemsGroup workItemsGroup = CreateWorkItemsGroup(int.MaxValue, wigStartInfo);

            ManualResetEvent anActionCompleted = new ManualResetEvent(false);

            ChoiceIndex choiceIndex = new ChoiceIndex();

            int i = 0;
            foreach (Action action in actions)
            {
                Action act = action;
                int value = i;
                workItemsGroup.QueueWorkItem(() => { act(); Interlocked.CompareExchange(ref choiceIndex._index, value, -1); anActionCompleted.Set(); });
                ++i;
            }
            workItemsGroup.Start();
            anActionCompleted.WaitOne();

            return choiceIndex._index;
        }

        public int Choice(params Action[] actions)
        {
            return Choice((IEnumerable<Action>)actions);
        }

        public void Pipe<T>(T pipeState, IEnumerable<Action<T>> actions)
        {
            WIGStartInfo wigStartInfo = new WIGStartInfo { StartSuspended = true };
            IWorkItemsGroup workItemsGroup = CreateWorkItemsGroup(1, wigStartInfo);
            foreach (Action<T> action in actions)
            {
                Action<T> act = action;
                workItemsGroup.QueueWorkItem(() => act(pipeState));
            }
            workItemsGroup.Start();
            workItemsGroup.WaitForIdle();
        }

        public void Pipe<T>(T pipeState, params Action<T>[] actions)
        {
            Pipe(pipeState, (IEnumerable<Action<T>>)actions);
        }
        #endregion
    }
    #endregion
}