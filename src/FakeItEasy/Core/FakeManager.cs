namespace FakeItEasy.Core
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
#if FEATURE_BINARY_SERIALIZATION
    using System.Runtime.Serialization;
#endif
    using System.Threading;

    /// <summary>
    /// The central point in the API for proxied fake objects handles interception
    /// of fake object calls by using a set of rules. User defined rules can be inserted
    /// by using the AddRule-method.
    /// </summary>
#if FEATURE_BINARY_SERIALIZATION
    [Serializable]
#endif
    public partial class FakeManager : IFakeCallProcessor
    {
#pragma warning disable CA2235 // Mark all non-serializable fields
        private static readonly SharedFakeObjectCallRule[] PostUserRules =
        {
            new ObjectMemberRule(),
            new AutoFakePropertyRule(),
            new PropertySetterRule(),
            new CancellationRule(),
            new DefaultReturnValueRule(),
        };
#pragma warning restore CA2235 // Mark all non-serializable fields

        private readonly IFakeObjectCallRule[] preUserRules;
#pragma warning disable CA2235 // Mark all non-serializable fields
        private readonly LinkedList<IInterceptionListener> interceptionListeners;
        private readonly WeakReference objectReference;
#pragma warning restore CA2235 // Mark all non-serializable fields

#if FEATURE_BINARY_SERIALIZATION
        [NonSerialized]
#endif
        private ConcurrentQueue<CompletedFakeObjectCall> recordedCalls;

        private int lastSequenceNumber = -1;

        /// <summary>
        /// Initializes a new instance of the <see cref="FakeManager"/> class.
        /// </summary>
        /// <param name="fakeObjectType">The faked type.</param>
        /// <param name="proxy">The faked proxy object.</param>
        internal FakeManager(Type fakeObjectType, object proxy)
        {
            Guard.AgainstNull(fakeObjectType, nameof(fakeObjectType));
            Guard.AgainstNull(proxy, nameof(proxy));

            this.objectReference = new WeakReference(proxy);
            this.FakeObjectType = fakeObjectType;

            this.preUserRules = new IFakeObjectCallRule[]
            {
                new EventRule(this),
            };
            this.AllUserRules = new LinkedList<CallRuleMetadata>();

            this.recordedCalls = new ConcurrentQueue<CompletedFakeObjectCall>();
            this.interceptionListeners = new LinkedList<IInterceptionListener>();
        }

        /// <summary>
        /// A delegate responsible for creating FakeObject instances.
        /// </summary>
        /// <param name="fakeObjectType">The faked type.</param>
        /// <param name="proxy">The faked proxy object.</param>
        /// <returns>An instance of <see cref="FakeManager"/>.</returns>
        internal delegate FakeManager Factory(Type fakeObjectType, object proxy);

        /// <summary>
        /// Gets the faked proxy object.
        /// </summary>
#pragma warning disable CA1716, CA1720 // Identifier contains keyword, Identifier contains type name
        public virtual object Object => this.objectReference.Target;
#pragma warning restore CA1716, CA1720 // Identifier contains keyword, Identifier contains type name

        /// <summary>
        /// Gets the faked type.
        /// </summary>
#pragma warning disable CA2235 // Mark all non-serializable fields
        public virtual Type FakeObjectType { get; }
#pragma warning restore CA2235 // Mark all non-serializable fields

        /// <summary>
        /// Gets the interceptions that are currently registered with the fake object.
        /// </summary>
        public virtual IEnumerable<IFakeObjectCallRule> Rules
        {
            get { return this.AllUserRules.Select(x => x.Rule); }
        }

#pragma warning disable CA2235 // Mark all non-serializable fields
        internal LinkedList<CallRuleMetadata> AllUserRules { get; }
#pragma warning restore CA2235 // Mark all non-serializable fields

        /// <summary>
        /// Adds a call rule to the fake object.
        /// </summary>
        /// <param name="rule">The rule to add.</param>
        public virtual void AddRuleFirst(IFakeObjectCallRule rule)
        {
            this.AllUserRules.AddFirst(new CallRuleMetadata { Rule = rule });
        }

        /// <summary>
        /// Adds a call rule last in the list of user rules, meaning it has the lowest priority possible.
        /// </summary>
        /// <param name="rule">The rule to add.</param>
        public virtual void AddRuleLast(IFakeObjectCallRule rule)
        {
            this.AllUserRules.AddLast(new CallRuleMetadata { Rule = rule });
        }

        /// <summary>
        /// Removes the specified rule for the fake object.
        /// </summary>
        /// <param name="rule">The rule to remove.</param>
        public virtual void RemoveRule(IFakeObjectCallRule rule)
        {
            Guard.AgainstNull(rule, nameof(rule));

            var ruleToRemove = this.AllUserRules.FirstOrDefault(x => x.Rule.Equals(rule));
            this.AllUserRules.Remove(ruleToRemove);
        }

        /// <summary>
        /// Adds an interception listener to the manager.
        /// </summary>
        /// <param name="listener">The listener to add.</param>
        public void AddInterceptionListener(IInterceptionListener listener)
        {
            this.interceptionListeners.AddFirst(listener);
        }

        [SuppressMessage("Microsoft.Design", "CA1033:InterfaceMethodsShouldBeCallableByChildTypes", Justification = "Explicit implementation to be able to make the IFakeCallProcessor interface internal.")]
        void IFakeCallProcessor.Process(InterceptedFakeObjectCall fakeObjectCall)
        {
            foreach (var listener in this.interceptionListeners)
            {
                listener.OnBeforeCallIntercepted(fakeObjectCall);
            }

            IFakeObjectCallRule ruleUsed = null;
            try
            {
                this.ApplyBestRule(fakeObjectCall, out ruleUsed);
            }
            finally
            {
                var readonlyCall = fakeObjectCall.AsReadOnly();

                this.RecordCall(readonlyCall);

                foreach (var listener in this.interceptionListeners.Reverse())
                {
                    listener.OnAfterCallIntercepted(readonlyCall, ruleUsed);
                }
            }
        }

        internal int GetLastRecordedSequenceNumber() => this.lastSequenceNumber;

        /// <summary>
        /// Returns a list of all calls on the managed object.
        /// </summary>
        /// <returns>A list of all calls on the managed object.</returns>
        internal IEnumerable<CompletedFakeObjectCall> GetRecordedCalls()
        {
            return this.recordedCalls;
        }

        /// <summary>
        /// Removes any specified user rules.
        /// </summary>
        internal virtual void ClearUserRules()
        {
            this.AllUserRules.Clear();
        }

        /// <summary>
        /// Removes any recorded calls.
        /// </summary>
        internal virtual void ClearRecordedCalls()
        {
            this.recordedCalls = new ConcurrentQueue<CompletedFakeObjectCall>();
        }

        /// <summary>
        /// Adds a call rule to the fake object after the specified rule.
        /// </summary>
        /// <param name="previousRule">The rule after which to add a rule.</param>
        /// <param name="newRule">The rule to add.</param>
        internal void AddRuleAfter(IFakeObjectCallRule previousRule, IFakeObjectCallRule newRule)
        {
            var previousNode = this.AllUserRules.Nodes().FirstOrDefault(n => n.Value.Rule == previousRule);
            if (previousNode == null)
            {
                throw new InvalidOperationException("The rule after which to add the new rule was not found in the list.");
            }

            this.AllUserRules.AddAfter(previousNode, new CallRuleMetadata { Rule = newRule });
        }

        // Apply the best rule to the call. There will always be at least one applicable rule, and the rule used will
        // be returned in the ruleUsed out parameter. An out parameter was used instead of the return value because it
        // made it easier to return the value even when the rule throws an exception. Another solution might have been
        // to catch the exception, which would then need to be bundled into a return object with the rule and eventually
        // rethrown.
        private void ApplyBestRule(IInterceptedFakeObjectCall fakeObjectCall, out IFakeObjectCallRule ruleUsed)
        {
            foreach (var preUserRule in this.preUserRules)
            {
                if (preUserRule.IsApplicableTo(fakeObjectCall))
                {
                    ruleUsed = preUserRule;
                    preUserRule.Apply(fakeObjectCall);
                    return;
                }
            }

            foreach (var rule in this.AllUserRules)
            {
                if (rule.Rule.IsApplicableTo(fakeObjectCall) && rule.HasNotBeenCalledSpecifiedNumberOfTimes())
                {
                    ruleUsed = rule.Rule;
                    rule.CalledNumberOfTimes++;
                    rule.Rule.Apply(fakeObjectCall);
                    return;
                }
            }

            foreach (var postUserRule in PostUserRules)
            {
                if (postUserRule.IsApplicableTo(fakeObjectCall))
                {
                    ruleUsed = postUserRule;
                    postUserRule.Apply(fakeObjectCall);
                    return;
                }
            }

            // The last rule in the postUserRules is applicable to all calls, so this line is here to satisfy
            // the compiler. ruleUsed will always have been set and we will have returned before now.
            ruleUsed = null;
        }

        /// <summary>
        /// Records that a call has occurred on the managed object.
        /// </summary>
        /// <param name="call">The call to remember.</param>
        private void RecordCall(CompletedFakeObjectCall call)
        {
            this.UpdateLastSequenceNumber(call.SequenceNumber);
            this.recordedCalls.Enqueue(call);
        }

        private void UpdateLastSequenceNumber(int sequenceNumber)
        {
            //// Set the specified sequence number as the last sequence number if it's greater than the current last sequence number.
            //// We use this number in FakeAsserter to separate calls made before the assertion starts from those made during the
            //// assertion.
            //// Because lastSequenceNumber might be changed by another thread after the comparison, we use CompareExchange to
            //// only assign it if it has the same value as the one we compared with. If it's not the case, we retry.

            int last;
            do
            {
                last = this.lastSequenceNumber;
            }
            while (sequenceNumber > last &&
                   sequenceNumber != Interlocked.CompareExchange(ref this.lastSequenceNumber, sequenceNumber, last));
        }

#if FEATURE_BINARY_SERIALIZATION
        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
            this.recordedCalls = new ConcurrentQueue<CompletedFakeObjectCall>();
        }
#endif

        private abstract class SharedFakeObjectCallRule : IFakeObjectCallRule
        {
            int? IFakeObjectCallRule.NumberOfTimesToCall => null;

            public abstract bool IsApplicableTo(IFakeObjectCall fakeObjectCall);

            public abstract void Apply(IInterceptedFakeObjectCall fakeObjectCall);
        }
    }
}
