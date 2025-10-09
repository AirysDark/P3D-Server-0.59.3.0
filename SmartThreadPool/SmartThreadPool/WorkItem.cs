using System;
using System.Threading;
using System.Diagnostics;
using Amib.Threading;

namespace Amib.Threading.Internal
{
    /// <summary>
    /// Represents a queued work item for SmartThreadPool.
    /// </summary>
    public partial class WorkItem : IHasWorkItemPriority
    {
        #region WorkItemState enum
        private enum WorkItemState
        {
            InQueue = 0,
            InProgress = 1,
            Completed = 2,
            Canceled = 3,
        }

        private static bool IsValidStatesTransition(WorkItemState currentState, WorkItemState nextState)
        {
            switch (currentState)
            {
                case WorkItemState.InQueue:
                    return nextState == WorkItemState.InProgress || nextState == WorkItemState.Canceled;
                case WorkItemState.InProgress:
                    return nextState == WorkItemState.Completed || nextState == WorkItemState.Canceled;
                default:
                    return false;
            }
        }
        #endregion

        #region Fields
        private readonly WorkItemCallback _callback;
        private object _state;

#if (NETFRAMEWORK)
        private readonly CallerThreadContext _callerContext;
#endif
        private object _result;
        private Exception _exception;
        private WorkItemState _workItemState;
        private ManualResetEvent _workItemCompleted;
        private int _workItemCompletedRefCount;
        private readonly WorkItemResult _workItemResult;
        private readonly WorkItemInfo _workItemInfo;
        private event WorkItemStateCallback _workItemStartedEvent;
        private event WorkItemStateCallback _workItemCompletedEvent;
        private CanceledWorkItemsGroup _canceledWorkItemsGroup = CanceledWorkItemsGroup.NotCanceledWorkItemsGroup;
        private CanceledWorkItemsGroup _canceledSmartThreadPool = CanceledWorkItemsGroup.NotCanceledWorkItemsGroup;
        private readonly IWorkItemsGroup _workItemsGroup;
        private Thread _executingThread;
        private long _expirationTime;
        private Stopwatch _waitingOnQueueStopwatch;
        private Stopwatch _processingStopwatch;
        #endregion

        #region Properties
        public TimeSpan WaitingTime => _waitingOnQueueStopwatch.Elapsed;
        public TimeSpan ProcessTime => _processingStopwatch.Elapsed;
        internal WorkItemInfo WorkItemInfo => _workItemInfo;
        #endregion

        #region Construction
        public WorkItem(IWorkItemsGroup workItemsGroup, WorkItemInfo workItemInfo, WorkItemCallback callback, object state)
        {
            _workItemsGroup = workItemsGroup;
            _workItemInfo = workItemInfo;

#if (NETFRAMEWORK)
            if (_workItemInfo.UseCallerCallContext || _workItemInfo.UseCallerHttpContext)
                _callerContext = CallerThreadContext.Capture(_workItemInfo.UseCallerCallContext, _workItemInfo.UseCallerHttpContext);
#endif
            _callback = callback;
            _state = state;
            _workItemResult = new WorkItemResult(this);
            Initialize();
        }

        internal void Initialize()
        {
            _workItemState = WorkItemState.InQueue;
            _workItemCompleted = null;
            _workItemCompletedRefCount = 0;
            _waitingOnQueueStopwatch = new Stopwatch();
            _processingStopwatch = new Stopwatch();
            _expirationTime = _workItemInfo.Timeout > 0
                ? DateTime.UtcNow.Ticks + _workItemInfo.Timeout * TimeSpan.TicksPerMillisecond
                : long.MaxValue;
        }

        internal bool WasQueuedBy(IWorkItemsGroup workItemsGroup) => workItemsGroup == _workItemsGroup;
        #endregion

        #region Methods (execution lifecycle)
        internal CanceledWorkItemsGroup CanceledWorkItemsGroup { get => _canceledWorkItemsGroup; set => _canceledWorkItemsGroup = value; }
        internal CanceledWorkItemsGroup CanceledSmartThreadPool { get => _canceledSmartThreadPool; set => _canceledSmartThreadPool = value; }

        public bool StartingWorkItem()
        {
            _waitingOnQueueStopwatch.Stop();
            _processingStopwatch.Start();

            lock (this)
            {
                if (IsCanceled)
                {
                    bool result = false;
                    if (_workItemInfo.PostExecuteWorkItemCallback != null &&
                        (_workItemInfo.CallToPostExecute & CallToPostExecute.WhenWorkItemCanceled) != 0)
                        result = true;
                    return result;
                }

                _executingThread = Thread.CurrentThread;
                SetWorkItemState(WorkItemState.InProgress);
            }

            return true;
        }

        public void Execute()
        {
            CallToPostExecute currentCallToPostExecute = 0;

            switch (GetWorkItemState())
            {
                case WorkItemState.InProgress:
                    currentCallToPostExecute |= CallToPostExecute.WhenWorkItemNotCanceled;
                    ExecuteWorkItem();
                    break;
                case WorkItemState.Canceled:
                    currentCallToPostExecute |= CallToPostExecute.WhenWorkItemCanceled;
                    break;
                default:
                    throw new NotSupportedException();
            }

            if ((currentCallToPostExecute & _workItemInfo.CallToPostExecute) != 0)
                PostExecute();

            _processingStopwatch.Stop();
        }

        internal void FireWorkItemCompleted()
        {
            try { _workItemCompletedEvent?.Invoke(this); } catch { }
        }

        internal void FireWorkItemStarted()
        {
            try { _workItemStartedEvent?.Invoke(this); } catch { }
        }

        private void ExecuteWorkItem()
        {
            Exception exception = null;
            object result = null;

            try
            {
#if (NETFRAMEWORK)
                CallerThreadContext ctc = null;
                if (_callerContext != null)
                {
                    ctc = CallerThreadContext.Capture(_callerContext.CapturedCallContext, _callerContext.CapturedHttpContext);
                    CallerThreadContext.Apply(_callerContext);
                }
#endif
                try { result = _callback(_state); }
                catch (Exception e) { exception = e; }
                finally
                {
#if (NETFRAMEWORK)
                    if (_callerContext != null)
                    {
                        CallerThreadContext.Apply(ctc);
                    }
#endif
                    // mark that no thread is executing us anymore
                    Interlocked.Exchange(ref _executingThread, null);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SmartThreadPool] Worker exception: {ex}");
                exception = ex;
            }

            if (!SmartThreadPool.IsWorkItemCanceled)
                SetResult(result, exception);
        }

        private void PostExecute()
        {
            if (_workItemInfo.PostExecuteWorkItemCallback != null)
            {
                try { _workItemInfo.PostExecuteWorkItemCallback(_workItemResult); }
                catch (Exception e) { Debug.WriteLine($"[SmartThreadPool] PostExecute error: {e}"); }
            }
        }

        internal void SetResult(object result, Exception exception)
        {
            _result = result;
            _exception = exception;
            SignalComplete(false);
        }

        internal IWorkItemResult GetWorkItemResult() => _workItemResult;

        private WorkItemState GetWorkItemState()
        {
            lock (this)
            {
                if (_workItemState == WorkItemState.Completed)
                    return _workItemState;

                if (_workItemState != WorkItemState.Canceled &&
                    DateTime.UtcNow.Ticks > _expirationTime)
                    _workItemState = WorkItemState.Canceled;

                if (_workItemState == WorkItemState.InProgress)
                    return _workItemState;

                if (CanceledSmartThreadPool.IsCanceled || CanceledWorkItemsGroup.IsCanceled)
                    return WorkItemState.Canceled;

                return _workItemState;
            }
        }

        private void SetWorkItemState(WorkItemState workItemState)
        {
            lock (this)
            {
                if (IsValidStatesTransition(_workItemState, workItemState))
                    _workItemState = workItemState;
            }
        }

        private void SignalComplete(bool canceled)
        {
            SetWorkItemState(canceled ? WorkItemState.Canceled : WorkItemState.Completed);
            lock (this)
            {
                _workItemCompleted?.Set();
            }
        }

        internal void WorkItemIsQueued() => _waitingOnQueueStopwatch.Start();

        private bool Cancel(bool abortExecution)
        {
            bool success = false;
            bool signalComplete = false;

            lock (this)
            {
                switch (GetWorkItemState())
                {
                    case WorkItemState.Canceled:
                        if (abortExecution)
                        {
                            Thread t = Interlocked.CompareExchange(ref _executingThread, null, _executingThread);
                            if (t != null) { try { t.Interrupt(); } catch { } }
                        }
                        success = true;
                        break;

                    case WorkItemState.Completed:
                        break;

                    case WorkItemState.InProgress:
                        if (abortExecution)
                        {
                            Thread t = Interlocked.CompareExchange(ref _executingThread, null, _executingThread);
                            if (t != null) { try { t.Interrupt(); } catch { } }
                            success = true;
                            signalComplete = true;
                        }
                        else
                        {
                            success = true;
                            signalComplete = true;
                        }
                        break;

                    case WorkItemState.InQueue:
                        signalComplete = true;
                        success = true;
                        break;
                }

                if (signalComplete)
                    SignalComplete(true);
            }

            return success;
        }

        private bool IsCompleted
        {
            get
            {
                lock (this)
                {
                    var s = GetWorkItemState();
                    return s == WorkItemState.Completed || s == WorkItemState.Canceled;
                }
            }
        }

        public bool IsCanceled
        {
            get
            {
                lock (this)
                {
                    return GetWorkItemState() == WorkItemState.Canceled;
                }
            }
        }
        #endregion

        #region Result retrieval (required by WorkItemResult)
        // These two overloads are what your errors are complaining about.
        // They are called by WorkItemResult to retrieve the result (or throw) with timeout/cancel support.

        internal object GetResult(int millisecondsTimeout, bool exitContext, WaitHandle cancelWaitHandle)
        {
            Exception e;
            object result = GetResult(millisecondsTimeout, exitContext, cancelWaitHandle, out e);
            if (e != null)
                throw new WorkItemResultException("The work item caused an exception, see inner exception for details", e);
            return result;
        }

        internal object GetResult(int millisecondsTimeout, bool exitContext, WaitHandle cancelWaitHandle, out Exception e)
        {
            e = null;

            // immediate cancel?
            if (GetWorkItemState() == WorkItemState.Canceled)
                throw new WorkItemCancelException("Work item canceled");

            // already done?
            if (IsCompleted)
            {
                e = _exception;
                return _result;
            }

            if (cancelWaitHandle == null)
            {
                WaitHandle wh = GetWaitHandle();
                bool timeout = !STPEventWaitHandle.WaitOne(wh, millisecondsTimeout, exitContext);
                ReleaseWaitHandle();

                if (timeout)
                    throw new WorkItemTimeoutException("Work item timeout");
            }
            else
            {
                WaitHandle wh = GetWaitHandle();
                int result = STPEventWaitHandle.WaitAny(new WaitHandle[] { wh, cancelWaitHandle }, millisecondsTimeout, exitContext);
                ReleaseWaitHandle();

                switch (result)
                {
                    case 0:
                        // completed
                        break;
                    case 1:
                    case STPEventWaitHandle.WaitTimeout:
                        throw new WorkItemTimeoutException("Work item timeout");
                    default:
                        Debug.Assert(false);
                        break;
                }
            }

            if (GetWorkItemState() == WorkItemState.Canceled)
                throw new WorkItemCancelException("Work item canceled");

            Debug.Assert(IsCompleted);
            e = _exception;
            return _result;
        }
        #endregion

        #region WaitAll / WaitAny helpers
        internal static bool WaitAll(IWaitableResult[] waitableResults, int millisecondsTimeout, bool exitContext, WaitHandle cancelWaitHandle)
        {
            if (waitableResults == null) throw new ArgumentNullException(nameof(waitableResults));
            if (waitableResults.Length == 0) return true;

            bool success;
            var waitHandles = new WaitHandle[waitableResults.Length];
            GetWaitHandles(waitableResults, waitHandles);

            if (cancelWaitHandle == null && waitHandles.Length <= 64)
            {
                success = STPEventWaitHandle.WaitAll(waitHandles, millisecondsTimeout, exitContext);
            }
            else
            {
                success = true;
                int millisecondsLeft = millisecondsTimeout;
                var stopwatch = Stopwatch.StartNew();
                bool waitInfinitely = (Timeout.Infinite == millisecondsTimeout);

                var whs = (cancelWaitHandle != null)
                    ? new WaitHandle[] { null, cancelWaitHandle }
                    : new WaitHandle[] { null };

                for (int i = 0; i < waitableResults.Length; ++i)
                {
                    if (!waitInfinitely && millisecondsLeft < 0)
                    {
                        success = false;
                        break;
                    }

                    whs[0] = waitHandles[i];
                    int result = STPEventWaitHandle.WaitAny(whs, millisecondsLeft, exitContext);

                    if ((cancelWaitHandle != null && result == 1) || result == STPEventWaitHandle.WaitTimeout)
                    {
                        success = false;
                        break;
                    }

                    if (!waitInfinitely)
                        millisecondsLeft = millisecondsTimeout - (int)stopwatch.ElapsedMilliseconds;
                }
            }

            ReleaseWaitHandles(waitableResults);
            return success;
        }

        internal static int WaitAny(IWaitableResult[] waitableResults, int millisecondsTimeout, bool exitContext, WaitHandle cancelWaitHandle)
        {
            if (waitableResults == null) throw new ArgumentNullException(nameof(waitableResults));
            if (waitableResults.Length == 0) return STPEventWaitHandle.WaitTimeout;

            WaitHandle[] waitHandles;

            if (cancelWaitHandle != null)
            {
                waitHandles = new WaitHandle[waitableResults.Length + 1];
                GetWaitHandles(waitableResults, waitHandles);
                waitHandles[waitableResults.Length] = cancelWaitHandle;
            }
            else
            {
                waitHandles = new WaitHandle[waitableResults.Length];
                GetWaitHandles(waitableResults, waitHandles);
            }

            int result = STPEventWaitHandle.WaitAny(waitHandles, millisecondsTimeout, exitContext);

            if (cancelWaitHandle != null && result == waitableResults.Length)
                result = STPEventWaitHandle.WaitTimeout;

            ReleaseWaitHandles(waitableResults);
            return result;
        }

        private static void GetWaitHandles(IWaitableResult[] waitableResults, WaitHandle[] waitHandles)
        {
            for (int i = 0; i < waitableResults.Length; ++i)
            {
                var wir = waitableResults[i].GetWorkItemResult() as WorkItemResult;
                Debug.Assert(wir != null, "All waitableResults must be WorkItemResult objects");
                waitHandles[i] = wir.GetWorkItem().GetWaitHandle();
            }
        }

        private static void ReleaseWaitHandles(IWaitableResult[] waitableResults)
        {
            for (int i = 0; i < waitableResults.Length; ++i)
            {
                var wir = (WorkItemResult)waitableResults[i].GetWorkItemResult();
                wir.GetWorkItem().ReleaseWaitHandle();
            }
        }

        private WaitHandle GetWaitHandle()
        {
            lock (this)
            {
                if (_workItemCompleted == null)
                    _workItemCompleted = EventWaitHandleFactory.CreateManualResetEvent(IsCompleted);
                _workItemCompletedRefCount++;
                return _workItemCompleted;
            }
        }

        private void ReleaseWaitHandle()
        {
            lock (this)
            {
                if (_workItemCompleted != null)
                {
                    _workItemCompletedRefCount--;
                    if (_workItemCompletedRefCount == 0)
                    {
                        _workItemCompleted.Close();
                        _workItemCompleted = null;
                    }
                }
            }
        }
        #endregion

        #region IHasWorkItemPriority Members
        public WorkItemPriority WorkItemPriority => _workItemInfo.WorkItemPriority;
        #endregion

        internal event WorkItemStateCallback OnWorkItemStarted { add => _workItemStartedEvent += value; remove => _workItemStartedEvent -= value; }
        internal event WorkItemStateCallback OnWorkItemCompleted { add => _workItemCompletedEvent += value; remove => _workItemCompletedEvent -= value; }

        public void DisposeOfState()
        {
            if (_workItemInfo.DisposeOfStateObjects && _state is IDisposable disp)
            {
                disp.Dispose();
                _state = null;
            }
        }
    }
}