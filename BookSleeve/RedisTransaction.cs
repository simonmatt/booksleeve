﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;

namespace BookSleeve
{
    /// <summary>
    /// Represents a group of redis messages that will be sent as a single atomic 
    /// </summary>
    public sealed class RedisTransaction : RedisConnection
    {
        /// <summary>
        /// Features available to the redis server
        /// </summary>
        public override RedisFeatures Features { get { return parent.Features; } }
        /// <summary>
        /// The version of the connected redis server
        /// </summary>
        public override Version ServerVersion { get { return parent.ServerVersion; } }

        private RedisConnection parent;
        
        internal RedisTransaction(RedisConnection parent) : base(parent)
        {
            this.parent = parent;
        }
        /// <summary>
        /// Should a QUIT be sent when closing the connection?
        /// </summary>
        protected override bool QuitOnClose
        {
            get { return false; }
        }
        internal override Task Prepare(string[] scripts)
        {
            // do the SCRIPT LOAD outside of the transaction, since we don't
            // want to get odd race conditions
            return parent.Prepare(scripts);
        }
        internal override string GetScriptHash(string script)
        {
            // do the SCRIPT LOAD outside of the transaction, since we don't
            // want to get odd race conditions
            return parent.GetScriptHash(script);
        }
        
        /// <summary>
        /// Not supported, as nested transactions are not available.
        /// </summary>
        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Nested transactions are not supported", true)]
#pragma warning disable 809
        public override RedisTransaction CreateTransaction()
#pragma warning restore 809
        {
            throw new NotSupportedException("Nested transactions are not supported");
        }
        /// <summary>
        /// Release any resources held by this transaction.
        /// </summary>
        public override void Dispose()
        {
            base.Dispose();
            Discard();
        }
        /// <summary>
        /// Called before opening a connection
        /// </summary>
        protected override void OnOpening()
        {
            throw new InvalidOperationException("A transaction is linked to the parent connection, and does not require opening");
        }

        /// <summary>
        /// Sends all currently buffered commands to the redis server in a single unit; the transaction may subsequently be re-used to buffer additional blocks of commands if needed.
        /// </summary>
        public Task<bool> Execute(bool queueJump = false, object state = null)
        {
            var all = DequeueAll();
            if (all.Length == 0)
            {
                return AlwaysTrue;
            }
            var multiMessage = new MultiMessage(parent, all, conditions, state);
            conditions = null; // wipe
            parent.EnqueueMessage(multiMessage, queueJump);
            return multiMessage.Completion;
        }

        

        private List<Condition> conditions;
        /// <summary>
        /// Add a precondition to be enforced for this transaction
        /// </summary>
        public Task<bool> AddCondition(Condition condition)
        {
            if (condition == null) throw new ArgumentNullException("condition");
            if (conditions == null) conditions = new List<Condition>();
            conditions.Add(condition);
            return condition.Task;
        }

        /// <summary>
        /// Discards any buffered commands; the transaction may subsequently be re-used to buffer additional blocks of commands if needed.
        /// </summary>
        public void Discard()
        {
            CancelUnsent();
        }
    }
}

