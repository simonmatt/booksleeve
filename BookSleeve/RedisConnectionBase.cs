﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BookSleeve
{
    /// <summary>
    /// Base class for a redis-connection; provides core redis services
    /// </summary>
    public abstract class RedisConnectionBase : IDisposable
    {
        private Socket socket;
        private NetworkStream redisStream;

        private readonly Queue<RedisMessage> unsent;
        private readonly int port, ioTimeout, syncTimeout;
        private readonly string host, password;
        /// <summary>
        /// The amount of time to wait for any individual command to return a result when using Wait
        /// </summary>
        public int SyncTimeout { get { return syncTimeout; } }
        /// <summary>
        /// The host for the redis server
        /// </summary>
        public string Host { get { return host; } }
        /// <summary>
        /// The password used to authenticate with the redis server
        /// </summary>
        protected string Password { get { return password; } }
        /// <summary>
        /// The port for the redis server
        /// </summary>
        public int Port { get { return port; } }
        /// <summary>
        /// The IO timeout to use when communicating with the redis server
        /// </summary>
        protected int IOTimeout { get { return ioTimeout; } }
        private RedisFeatures features;
        /// <summary>
        /// Features available to the redis server
        /// </summary>
        public virtual RedisFeatures Features { get { return features; } }

        private string name;
        /// <summary>
        /// Specify a name for this connection (displayed via Server.ListClients / CLIENT LIST)
        /// </summary>
        public string Name
        {
            get { return name; }
            set
            {
                if (value != null)
                {
                    // apply same validation that Redis does
                    char c;
                    for (int i = 0; i < value.Length; i++)
                        if ((c = value[i]) < '!' || c > '~')
                            throw new ArgumentException("Client names cannot contain spaces, newlines or special characters.", "Name");
                }
                if (State == ConnectionState.New)
                {
                    this.name = value;
                }
                else
                {
                    throw new InvalidOperationException("Name can only be set on new connections");
                }
            }
        }

        /// <summary>
        /// The version of the connected redis server
        /// </summary>
        public virtual Version ServerVersion
        {
            get
            {
                var tmp = features;
                return tmp == null ? null : tmp.Version;
            }
        }
        /// <summary>
        /// Explicitly specify the server version; this is useful when INFO is not available
        /// </summary>
        public void SetServerVersion(Version version, ServerType type)
        {
            features = version == null ? null : new RedisFeatures(version);
            ServerType = type;
        }

        /// <summary>
        /// Obtains fresh statistics on the usage of the connection
        /// </summary>
        protected void GetCounterValues(out int messagesSent, out int messagesReceived,
            out int queueJumpers, out int messagesCancelled, out int unsent, out int errorMessages, out int timeouts)
        {
            messagesSent = Interlocked.CompareExchange(ref this.messagesSent, 0, 0);
            messagesReceived = Interlocked.CompareExchange(ref this.messagesReceived, 0, 0);
            queueJumpers = Interlocked.CompareExchange(ref this.queueJumpers, 0, 0);
            messagesCancelled = Interlocked.CompareExchange(ref this.messagesCancelled, 0, 0);
            messagesSent = Interlocked.CompareExchange(ref this.messagesSent, 0, 0);
            errorMessages = Interlocked.CompareExchange(ref this.errorMessages, 0, 0);
            timeouts = Interlocked.CompareExchange(ref this.timeouts, 0, 0);
            unsent = OutstandingCount;
        }
        /// <summary>
        /// Issues a basic ping/pong pair against the server, returning the latency
        /// </summary>
        protected Task<long> PingImpl(bool queueJump, bool duringInit = false, object state = null)
        {
            var msg = new PingMessage();
            if(duringInit) msg.DuringInit();
            return ExecuteInt64(msg, queueJump, state);
        }
        /// <summary>
        /// The default time to wait for individual commands to complete when using Wait
        /// </summary>
        protected const int DefaultSyncTimeout = 10000;
        // dont' really want external subclasses
        internal RedisConnectionBase(string host, int port = 6379, int ioTimeout = -1, string password = null, int maxUnsent = int.MaxValue,
            int syncTimeout = DefaultSyncTimeout)
        {
            if (syncTimeout <= 0) throw new ArgumentOutOfRangeException("syncTimeout");
            this.syncTimeout = syncTimeout;
            this.unsent = new Queue<RedisMessage>();
            this.host = host;
            this.port = port;
            this.ioTimeout = ioTimeout;
            this.password = password;

            IncludeDetailInTimeouts = true;

            this.sent = new Queue<RedisMessage>();
        }
        static bool TryParseVersion(string value, out Version version)
        {  // .NET 4.0 has Version.TryParse, but 3.5 CP does not
            var match = Regex.Match(value, "^[0-9.]+");
            if (match.Success) value = match.Value;
            try
            {
                version = new Version(value);
                return true;
            }
            catch
            {
                version = default(Version);
                return false;
            }
        }

        private int state;
        /// <summary>
        /// The current state of the connection
        /// </summary>
        public ConnectionState State
        {
            get { return (ConnectionState)state; }
        }
        /// <summary>
        /// Releases any resources associated with the connection
        /// </summary>
        public virtual void Dispose()
        {
            Close(false);
            abort = true;
            try { if (redisStream != null) redisStream.Dispose(); }
            catch { }
            try { if (outBuffer != null) outBuffer.Dispose(); }
            catch { }
            try { if (socket != null) {
                Trace("dispose", "closing socket...");
                socket.Close();
                Trace("dispose", "closed socket");
            } } catch { }
            socket = null;
            redisStream = null;
            outBuffer = null;
            Error = null;
            Trace("dispose", "done");
        }
        /// <summary>
        /// Called after opening a connection
        /// </summary>
        protected virtual void OnOpened() { }
        /// <summary>
        /// Called before opening a connection
        /// </summary>
        protected virtual void OnOpening() { }

        /// <summary>
        /// Called during connection init, but after the AUTH is sent (if needed)
        /// </summary>
        protected virtual void OnInitConnection() { }        

        [Conditional("VERBOSE")]
        internal static void Trace(string category, string message)
        {
#if VERBOSE
            System.Diagnostics.Trace.WriteLine(DateTime.Now.ToString("HH:mm:ss.ffff") + ": " + message, category);
#endif
        }
        [Conditional("VERBOSE")]
        internal static void Trace(string category, string message, params object[] args)
        {
#if VERBOSE
            Trace(category, string.Format(message, args));
#endif
        }

        /// <summary>
        /// Attempts to open the connection to the remote server
        /// </summary>
        public Task Open()
        {

            int foundState;
            if ((foundState = Interlocked.CompareExchange(ref state, (int)ConnectionState.Opening, (int)ConnectionState.New)) != (int)ConnectionState.New)
                throw new InvalidOperationException("Connection is " + (ConnectionState)foundState); // not shiny
            var source = new TaskCompletionSource<bool>();
            try
            {
                OnOpening();
                Task.Factory.StartNew(o => Connect(o), Tuple.Create(this, source), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                return source.Task;
            } catch(Exception ex)
            {
                source.TrySetException(ex);
                Interlocked.CompareExchange(ref state, (int)ConnectionState.Closed, (int)ConnectionState.Opening);
                throw;
            }
        }

        static void Connect(object state)
        {
            var tuple = (Tuple<RedisConnectionBase, TaskCompletionSource<bool>>)state;
            var conn = tuple.Item1;
            var source = tuple.Item2;
            Trace("connect", "sync");
            try
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;
                socket.SendTimeout = conn.ioTimeout;
                socket.Connect(new DnsEndPoint(conn.host, conn.port));
                conn.socket = socket;
                
                var readArgs = new SocketAsyncEventArgs();
                readArgs.Completed += conn.AsyncConnectReadCompleted;
                conn.readArgs = readArgs;
                conn.InitOutbound(source);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref conn.state, (int)ConnectionState.Closed, (int)ConnectionState.Opening);
                source.TrySetException(ex);
            }
        }

#if VERBOSE
        class CountingOutputStream : Stream
        {
            public override void Close()
            {
                tail.Close();
                base.Close();
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { tail.Dispose(); }
                base.Dispose(disposing);
            }
            private readonly Stream tail;
            public CountingOutputStream(Stream tail)
            {
                this.tail = tail;
            }
            private long position;
            public override long Position
            {
                get { return position; }
                set { throw new NotSupportedException(); }
            }
            public override void Write(byte[] buffer, int offset, int count)
            {
                tail.Write(buffer, offset, count);
                position += count;
            }
            public override void WriteByte(byte value)
            {
                tail.WriteByte(value);
                position++;
            }
            public override bool CanWrite
            {
                get { return true; }
            }
            public override void Flush()
            {
                tail.Flush();
            }
            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }
            public override long Length
            {
                get { return position; }
            }
            public override bool CanSeek
            {
                get { return false; }
            }
            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }
            public override bool CanRead
            {
                get { return false; }
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }
#endif
        private SocketAsyncEventArgs readArgs;
        void InitOutbound(TaskCompletionSource<bool> source)
        {
            try {
                readArgs.SetBuffer(buffer, 0, buffer.Length);
                //if (readArgs.SocketError != SocketError.Success)
                //{
                //    if (readArgs.ConnectByNameError != null) throw readArgs.ConnectByNameError;
                //    throw new InvalidOperationException("Socket error: " + readArgs.SocketError);
                //}
                //socket = readArgs.ConnectSocket;
                //socket.NoDelay = true;
                Trace("connected", socket.RemoteEndPoint.ToString());
                redisStream = new NetworkStream(socket);
                
                outBuffer = new BufferedStream(redisStream, 512); // buffer up operations
#if VERBOSE
                outBuffer = new CountingOutputStream(outBuffer); // so we can report the position etc
#endif
                redisStream.ReadTimeout = redisStream.WriteTimeout = ioTimeout;
                hold = false;
                Trace("init", "OnOpened");
                OnOpened();

                Trace("init", "start reading");
                bool haveData = ReadMoreAsync();

                if (!string.IsNullOrEmpty(password))
                {
                    var msg = RedisMessage.Create(-1, RedisLiteral.AUTH, password).ExpectOk().Critical();
                    msg.DuringInit();
                    EnqueueMessage(msg, true);
                }

                var asyncState = Tuple.Create(this, source);
                Task initTask;
                if (ServerVersion != null && ServerType != BookSleeve.ServerType.Unknown)
                { // no need to query for it; we already know what we need; use a PING instead
                    Trace("init", "ping/name");
                    initTask = TrySetName(true, duringInit: true, state: asyncState) ?? PingImpl(false, duringInit: true, state: asyncState);
                }
                else
                {
                    Trace("init", "get info");
                    var info = GetInfoImpl(null, true, duringInit:true, state: asyncState);
                    ContinueWith(info, initInfoCallback);
                    initTask = info;                    
                }
                ContinueWith(initTask, initCommandCallback);
                Trace("init", "OnInitConnection");
                OnInitConnection();

                EnqueueMessage(null, true); // start pushing (use this rather than WritePendingQueue to ensure no timing edge-cases with
                                            // other threads, etc);

                if (haveData) ReadReplyHeader();
            }
            catch (Exception ex)
            {
                Trace("init", ex.Message);
                source.TrySetException(ex);
                Interlocked.CompareExchange(ref state, (int)ConnectionState.Closed, (int)ConnectionState.Opening);
            }
        }
        static readonly Action<Task> initCommandCallback = task =>
        {
            var state = (Tuple<RedisConnectionBase, TaskCompletionSource<bool>>)task.AsyncState;
            var @this = state.Item1;
            var source = state.Item2;

            bool ok = true;
            Exception ex = null;
            if (task.IsFaulted)
            {
                ok = false;
                RedisException re;
                ex = task.Exception; 
                if (task.Exception.InnerExceptions.Count == 1 && (re = task.Exception.InnerExceptions[0] as RedisException) != null)
                {
                    ex = re;
                    ok = re.Message.StartsWith("ERR"); // means the command isn't available, but ultimately the server is
                                                       // talking to us, so I think we'll be just fine!
                }
            }
            if (ex != null)
            {
                @this.OnError("init command", ex, false);
            }
            if (ok)
            {
                Interlocked.CompareExchange(ref @this.state, (int)ConnectionState.Open, (int)ConnectionState.Opening);
                source.TrySetResult(true);
            }
            else
            {
                source.TrySetException(task.Exception);
                @this.Close(true);
                Interlocked.CompareExchange(ref @this.state, (int)ConnectionState.Closed, (int)ConnectionState.Opening);
            }
        };
        static readonly Action<Task<string>> initInfoCallback = task =>
        {
            var state = (Tuple<RedisConnectionBase, TaskCompletionSource<bool>>)task.AsyncState;
            var @this = state.Item1;
            if (!task.IsFaulted && task.IsCompleted)
            {
                try
                {
                    // process this when available
                    var parsed = ParseInfo(task.Result);
                    string s;
                    Version version = null;
                    if (parsed.TryGetValue("redis_version", out s))
                    {
                        if (!TryParseVersion(s, out version)) version = null;
                    }
                    ServerType serverType = ServerType.Unknown;

                    if (parsed.TryGetValue("redis_mode", out s) && s == "sentinel")
                    {
                        serverType = BookSleeve.ServerType.Sentinel;
                    }
                    else if (parsed.TryGetValue("role", out s) && s != null)
                    {
                        switch (s)
                        {
                            case "master": serverType = BookSleeve.ServerType.Master; break;
                            case "slave": serverType = BookSleeve.ServerType.Slave; break;
                        }
                    }
                    @this.SetServerVersion(version, serverType);
                    @this.TrySetName(true, duringInit: false);
                }
                catch (Exception ex) {
                    @this.OnError("parse info", ex, false);
                }
            }
        };
        /// <summary>
        /// Specify a name for the current connection
        /// </summary>
        protected Task TrySetName(bool queueJump, bool duringInit = false, object state = null)
        {
            if (!string.IsNullOrEmpty(name))
            {
                switch (ServerType)
                {
                    case ServerType.Master:
                    case ServerType.Slave:
                        var tmp = Features;
                        if (tmp != null && tmp.ClientName)
                        {
                            var msg = RedisMessage.Create(-1, RedisLiteral.CLIENT, RedisLiteral.SETNAME, name);
                            if (duringInit) msg.DuringInit();
                            return ExecuteVoid(msg, queueJump, state);
                        }
                        break;
                }
            }
            return null;
        }

        bool ReadMoreAsync()
        {
            Trace("read", "async");
            bufferOffset = bufferCount = 0;
            if (socket.ReceiveAsync(readArgs)) return false; // not yet available
            Trace("read", "data available immediately");
            if (readArgs.SocketError == SocketError.Success)
            {
                bufferCount = readArgs.BytesTransferred;
                return true; // completed and OK
            }

            // otherwise completed immediately but need to process errors etc
            AsyncConnectReadCompleted(socket, readArgs);
            return false;
        }
        void AsyncConnectReadCompleted(object sender, SocketAsyncEventArgs e)
        {
            try
            {
                Trace("receive", "< {0}, {1}, {2} bytes", e.LastOperation, e.SocketError, e.BytesTransferred);
                switch (e.LastOperation)
                {
                    case SocketAsyncOperation.Receive:
                        switch (readArgs.SocketError)
                        {
                            case SocketError.Success:
                                bufferCount = e.BytesTransferred;
                                ReadReplyHeader();
                                break;
                            default:
                                bufferCount = 0;
                                ReadReplyHeader();
                                break;
                        }
                        break;
                    default:
                        throw new NotImplementedException(e.LastOperation.ToString());
                }
            }
            catch (Exception ex)
            {
                OnError("async-read", ex, true);
            }
        }
        /// <summary>
        /// The INFO command returns information and statistics about the server in format that is simple to parse by computers and easy to red by humans.
        /// </summary>
        /// <remarks>http://redis.io/commands/info</remarks>
        [Obsolete("Please use .Server.GetInfo instead")]
        public Task<string> GetInfo(bool queueJump = false)
        {
            return GetInfoImpl(null, queueJump, false);
        }
        /// <summary>
        /// The INFO command returns information and statistics about the server in format that is simple to parse by computers and easy to red by humans.
        /// </summary>
        /// <remarks>http://redis.io/commands/info</remarks>
        [Obsolete("Please use .Server.GetInfo instead")]
        public Task<string> GetInfo(string category, bool queueJump = false)
        {
            return GetInfoImpl(category, queueJump, false);
        }

        internal Task<string> GetInfoImpl(string category, bool queueJump, bool duringInit, object state = null)
        {
            var msg = string.IsNullOrEmpty(category) ? RedisMessage.Create(-1, RedisLiteral.INFO) : RedisMessage.Create(-1, RedisLiteral.INFO, category);
            if (duringInit) msg.DuringInit();
            return ExecuteString(msg, queueJump, state);
        }

        internal static Dictionary<string, string> ParseInfo(string result)
        {
            string[] lines = result.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            var data = new Dictionary<string, string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line) || line[0] == '#') continue; // 2.6+ can have empty lines, and comment lines
                int idx = line.IndexOf(':');
                if (idx > 0) // double check this line looks about right
                {
                    data.Add(line.Substring(0, idx), line.Substring(idx + 1));
                }
            }
            return data;
        }

        int timeouts;

        /// <summary>
        /// Indicate the number of messages that have not yet been set.
        /// </summary>
        public virtual int OutstandingCount
        {
            get
            {
                lock (unsent) { return unsent.Count; }
            }
        }
      
        /// <summary>
        /// Raised when a connection becomes closed.
        /// </summary>
        public event EventHandler Closed;
        volatile bool abort;
        /// <summary>
        /// Closes the connection; either draining the unsent queue (to completion), or abandoning the unsent queue.
        /// </summary>
        public virtual void Close(bool abort)
        {
            this.abort |= abort;
            if (QuitOnClose)
            {
                switch (state)
                {
                    case (int)ConnectionState.Opening:
                    case (int)ConnectionState.Open:
                        Interlocked.Exchange(ref state, (int)ConnectionState.Closing);
                        if (hold || outBuffer != null)
                        {
                            Trace("close", "sending quit...");
                            Wait(ExecuteVoid(RedisMessage.Create(-1, RedisLiteral.QUIT), false));
                        }
                        break;
                }
            }
            hold = true;
        }

        /// <summary>
        /// Should a QUIT be sent when closing the connection?
        /// </summary>
        protected virtual bool QuitOnClose { get { return true; } }

//        private void ReadMoreAsync()
//        {
//#if VERBOSE
//            Trace.WriteLine(socketNumber + "? async");
//#endif
//            bufferOffset = bufferCount = 0;
//            var tmp = redisStream;
//            if (tmp != null)
//            {
//                tmp.BeginRead(buffer, 0, BufferSize, readReplyHeader, tmp); // read more IO here (in parallel)
//            }
//        }
        private bool ReadMoreSync()
        {
            Trace("read", "sync");
            var tmp = socket;
            if (tmp == null) return false;
            bufferOffset = bufferCount = 0;
            int bytesRead = tmp.Receive(buffer);
            Trace("read", "{0} bytes", bytesRead);
            if (bytesRead > 0)
            {
                bufferCount = bytesRead;
                return true;
            }
            return false;
        }
        private void ReadReplyHeader()
        {
            try
            {
                if (bufferCount <= 0 || socket == null)
                {   // EOF
                    Trace("< EOF", "received");
                    Shutdown("End of stream", null);
                }
                else
                {
                    bool isEof = false;
                MoreDataAvailable:
                    while (bufferCount > 0)
                    {
                        Trace("reply-header", "< {0} bytes buffered", bufferCount);
                        RedisResult result = ReadSingleResult();
                        Trace("reply-header", "< {0} bytes remain");
                        Interlocked.Increment(ref messagesReceived);
                        object ctx = ProcessReply(ref result);

                        if (result.IsError)
                        {
                            Interlocked.Increment(ref errorMessages);
                            OnError("Redis server", result.Error(), false);
                        }

                        var state = new Tuple<RedisConnectionBase, object, RedisResult>(this, ctx, result);
                        Task.Factory.StartNew(processCallbacks, state, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
                        Trace("reply-header", "check for more");
                        isEof = false;
                    }
                    Trace("reply-header", "@ buffer empty");
                    if (isEof)
                    {   // EOF
                        Shutdown("End of stream", null);
                    }
                    else
                    {
                        if (ReadMoreAsync()) goto MoreDataAvailable;
                    }
                }
            }
            catch (Exception ex)
            {
                Trace("reply-header", ex.Message);
                Shutdown("Invalid inbound stream", ex);
            }
        }
        private static readonly Action<object> processCallbacks = ProcessCallbacks;
        private static void ProcessCallbacks(object state)
        {
            var tuple = (Tuple<RedisConnectionBase, object, RedisResult>)state;
            try
            {
                Trace("callback", "processing callback for: {0}", tuple.Item3);
                tuple.Item1.ProcessCallbacks(tuple.Item2, tuple.Item3);
                Trace("callback", "processed callback");
            }
            catch (Exception ex)
            {
                Trace("callback", ex.Message);
                tuple.Item1.OnError("Processing callbacks", ex, false);
            }
        }


        /// <summary>
        /// Peek at the next item in the sent-queue
        /// </summary>
        internal RedisMessage PeekSent()
        {
            lock (sent)
            {
                return sent.Count == 0 ? null : sent.Peek();
            }
        }

        internal virtual object ProcessReply(ref RedisResult result)
        {
            RedisMessage message;
            lock (sent)
            {
                int count = sent.Count;
                if (count == 0) throw new RedisException("Data received with no matching message");
                message = sent.Dequeue();
                if (count == 1) Monitor.Pulse(sent); // in case the outbound stream is closing and needs to know we're up-to-date
            }
            return ProcessReply(ref result, message);
        }

        internal virtual object ProcessReply(ref RedisResult result, RedisMessage message)
        {
            byte[] expected;
            if (!result.IsError && (expected = message.Expected) != null)
            {
                result = result.IsMatch(expected)
                ? RedisResult.Pass : RedisResult.Error(result.ValueString);
            }

            if (result.IsError && message.MustSucceed)
            {
                throw new RedisException("A critical operation failed: " + message.ToString());
            }
            return message;
        }
        internal virtual void ProcessCallbacks(object ctx, RedisResult result)
        {
            if(ctx != null) CompleteMessage((RedisMessage)ctx, result);
        }

        private RedisResult ReadSingleResult()
        {
            byte b = ReadByteOrFail();
            switch ((char)b)
            {
                case '+':
                    return RedisResult.Message(ReadBytesToCrlf());
                case '-':
                    return RedisResult.Error(ReadStringToCrlf());
                case ':':
                    return RedisResult.Integer(ReadInt64());
                case '$':
                    return RedisResult.Bytes(ReadBulkBytes());
                case '*':
                    int count = (int)ReadInt64();
                    if (count == -1) return RedisResult.Multi(null);
                    RedisResult[] inner = new RedisResult[count];
                    for (int i = 0; i < count; i++)
                    {
                        inner[i] = ReadSingleResult();
                    }
                    return RedisResult.Multi(inner);
                default:
                    throw new RedisException("Not expecting header: &x" + b.ToString("x2"));
            }
        }
        internal void CompleteMessage(RedisMessage message, RedisResult result)
        {
            try
            {
                message.Complete(result);
            }
            catch (Exception ex)
            {
                OnError("Completing message", ex, false);
            }
        }
        private void Shutdown(string cause, Exception error)
        {
            Close(error != null);
            Interlocked.CompareExchange(ref state, (int)ConnectionState.Closed, (int)ConnectionState.Closing);

            if (error != null) OnError(cause, error, true);
            ShuttingDown(error);
            Dispose();
            var handler = Closed;
            if (handler != null) handler(this, EventArgs.Empty);

        }
        /// <summary>
        /// Invoked when the server is terminating
        /// </summary>
        protected virtual void ShuttingDown(Exception error)
        {
            RedisMessage message;
            RedisResult result = null;

            lock (sent)
            {
                if (sent.Count > 0)
                {
                    result = RedisResult.Error(
                        error == null ? "The server terminated before a reply was received"
                        : ("Error processing data: " + error.Message));
                }
                while (sent.Count > 0)
                { // notify clients of things that just didn't happen

                    message = sent.Dequeue();
                    CompleteMessage(message, result);
                }
            }
        }
        private readonly Queue<RedisMessage> sent;



        private static readonly byte[] empty = new byte[0];
        private int Read(byte[] scratch, int offset, int maxBytes)
        {
            if (bufferCount > 0 || ReadMoreSync())
            {
                int count = Math.Min(maxBytes, bufferCount);
                Buffer.BlockCopy(buffer, bufferOffset, scratch, offset, count);
                bufferOffset += count;
                bufferCount -= count;
                return count;
            }
            else
            {
                return 0;
            }
        }
        private byte[] ReadBulkBytes()
        {
            int len;
            checked
            {
                len = (int)ReadInt64();
            }
            switch (len)
            {
                case -1: return null;
                case 0: BurnCrlf(); return empty;
            }
            byte[] data = new byte[len];
            int bytesRead, offset = 0;
            while (len > 0 && (bytesRead = Read(data, offset, len)) > 0)
            {
                len -= bytesRead;
                offset += bytesRead;
            }
            if (len > 0) throw new EndOfStreamException("EOF reading bulk-bytes");
            BurnCrlf();
            return data;
        }
        private byte ReadByteOrFail()
        {
            if (bufferCount > 0 || ReadMoreSync())
            {
                bufferCount--;
                return buffer[bufferOffset++];
            }
            throw new EndOfStreamException();
        }
        private void BurnCrlf()
        {
            if (ReadByteOrFail() != (byte)'\r' || ReadByteOrFail() != (byte)'\n') throw new InvalidDataException("Expected crlf terminator not found");
        }

        const int BufferSize = 2048;
        private readonly byte[] buffer = new byte[BufferSize];
        int bufferOffset = 0, bufferCount = 0;

        private byte[] ReadBytesToCrlf()
        {
            // check for data inside the buffer first
            int bytes = FindCrlfInBuffer();
            byte[] result;
            if (bytes >= 0)
            {
                result = new byte[bytes];
                Buffer.BlockCopy(buffer, bufferOffset, result, 0, bytes);
                // subtract the data; don't forget to include the CRLF
                bufferCount -= (bytes + 2);
                bufferOffset += (bytes + 2);
            }
            else
            {
                byte[] oversizedBuffer;
                int len = FillBodyBufferToCrlf(out oversizedBuffer);
                result = new byte[len];
                Buffer.BlockCopy(oversizedBuffer, 0, result, 0, len);
            }


            return result;
        }
        int FindCrlfInBuffer()
        {
            int max = bufferOffset + bufferCount - 1;
            for (int i = bufferOffset; i < max; i++)
            {
                if (buffer[i] == (byte)'\r' && buffer[i + 1] == (byte)'\n')
                {
                    int bytes = i - bufferOffset;
                    return bytes;
                }
            }
            return -1;
        }
        private string ReadStringToCrlf()
        {
            // check for data inside the buffer first
            int bytes = FindCrlfInBuffer();
            string result;
            if (bytes >= 0)
            {
                result = Encoding.UTF8.GetString(buffer, bufferOffset, bytes);
                // subtract the data; don't forget to include the CRLF
                bufferCount -= (bytes + 2);
                bufferOffset += (bytes + 2);
            }
            else
            {
                // check for data that steps over the buffer
                byte[] oversizedBuffer;
                int len = FillBodyBufferToCrlf(out oversizedBuffer);
                result = Encoding.UTF8.GetString(oversizedBuffer, 0, len);
            }
            return result;
        }

        private int FillBodyBufferToCrlf(out byte[] oversizedBuffer)
        {
            bool haveCr = false;
            bodyBuffer.SetLength(0);
            byte b;
            do
            {
                b = ReadByteOrFail();
                if (haveCr)
                {
                    if (b == (byte)'\n')
                    {// we have our string
                        oversizedBuffer = bodyBuffer.GetBuffer();
                        return (int)bodyBuffer.Length;
                    }
                    else
                    {
                        bodyBuffer.WriteByte((byte)'\r');
                        haveCr = false;
                    }
                }
                if (b == (byte)'\r')
                {
                    haveCr = true;
                }
                else
                {
                    bodyBuffer.WriteByte(b);
                }
            } while (true);
        }

        private long ReadInt64()
        {
            byte[] oversizedBuffer;
            int len = FillBodyBufferToCrlf(out oversizedBuffer);
            // crank our own int parser... why not...
            int tmp;
            switch (len)
            {
                case 0:
                    throw new EndOfStreamException("No data parsing integer");
                case 1:
                    if ((tmp = ((int)oversizedBuffer[0] - '0')) >= 0 && tmp <= 9)
                    {
                        return tmp;
                    }
                    break;
            }
            bool isNeg = oversizedBuffer[0] == (byte)'-';
            if (isNeg && len == 2 && (tmp = ((int)oversizedBuffer[1] - '0')) >= 0 && tmp <= 9)
            {
                return -tmp;
            }

            long value = 0;
            for (int i = isNeg ? 1 : 0; i < len; i++)
            {
                if ((tmp = ((int)oversizedBuffer[i] - '0')) >= 0 && tmp <= 9)
                {
                    value = (value * 10) + tmp;
                }
                else
                {
                    throw new FormatException("Unable to parse integer: " + Encoding.UTF8.GetString(oversizedBuffer, 0, len));
                }
            }
            return isNeg ? -value : value;
        }

        /// <summary>
        /// Indicates the number of commands executed on a per-database basis
        /// </summary>
        protected Dictionary<int, int> GetDbUsage()
        {
            lock (dbUsage)
            {
                return new Dictionary<int, int>(dbUsage);
            }
        }
        int messagesSent, messagesReceived, queueJumpers, messagesCancelled, errorMessages;
        private readonly Dictionary<int, int> dbUsage = new Dictionary<int, int>();
        private void LogUsage(int db)
        {
            lock (dbUsage)
            {
                int count;
                if (dbUsage.TryGetValue(db, out count))
                {
                    dbUsage[db] = count + 1;
                }
                else
                {
                    dbUsage.Add(db, 1);
                }
            }
        }
        /// <summary>
        /// Invoked when any error message is received on the connection.
        /// </summary>
        public event EventHandler<ErrorEventArgs> Error;
        /// <summary>
        /// Raises an error event
        /// </summary>
        protected void OnError(object sender, ErrorEventArgs args)
        {
            var handler = Error;
            if (handler != null)
            {
                handler(sender, args);
            }
        }
        /// <summary>
        /// Raises an error event
        /// </summary>
        protected void OnError(string cause, Exception ex, bool isFatal)
        {
            var handler = Error;
            var agg = ex as AggregateException;
            if (handler == null)
            {
                if (agg != null)
                {
                    foreach (var inner in agg.InnerExceptions)
                    {
                        Trace(cause, inner.Message);
                    }
                }
                else
                {
                    Trace(cause, ex.Message);
                }
            }
            else
            {
                if (agg != null)
                {
                    foreach (var inner in agg.InnerExceptions)
                    {
                        handler(this, new ErrorEventArgs(inner, cause, isFatal));
                    }
                }
                else
                {
                    handler(this, new ErrorEventArgs(ex, cause, isFatal));
                }
            }
        }
        private Stream outBuffer;
        internal void Flush(bool all)
        {
            if (all)
            {
                var tmp1 = outBuffer;
                if (tmp1 != null) tmp1.Flush();
            }
            var tmp2 = redisStream;
            if(tmp2 != null) tmp2.Flush();
            Trace("send", all ? "full-flush" : "part-flush");
        }

        private int db = 0;
        //private void Outgoing()
        //{
        //    try
        //    {

        //        int db = 0;
        //        RedisMessage next;
        //        Trace.WriteLine("Redis send-pump is starting");
        //        bool isHigh, shouldFlush;
        //        while (unsent.TryDequeue(false, out next, out isHigh, out shouldFlush))
        //        {

        //            Flush(shouldFlush);

        //        }
        //        Interlocked.CompareExchange(ref state, (int)ConnectionState.Closing, (int)ConnectionState.Open);
        //        if (redisStream != null)
        //        {
        //            var quit = RedisMessage.Create(-1, RedisLiteral.QUIT).ExpectOk().Critical();

        //            RecordSent(quit, !abort);
        //            quit.Write(outBuffer);
        //            outBuffer.Flush();
        //            redisStream.Flush();
        //            Interlocked.Increment(ref messagesSent);
        //        }
        //        Trace.WriteLine("Redis send-pump is exiting");
        //    }
        //    catch (Exception ex)
        //    {
        //        OnError("Outgoing queue", ex, true);
        //    }

        //}

        internal void WriteMessage(ref int db, RedisMessage next, IList<QueuedMessage> queued)
        {
            var snapshot = outBuffer;
            if (snapshot == null)
            {
                throw new InvalidOperationException("Cannot write message; output is unavailable");
            }
            if (next.Db >= 0)
            {
                if (db != next.Db)
                {
                    db = next.Db;
                    RedisMessage changeDb = RedisMessage.Create(db, RedisLiteral.SELECT, db).ExpectOk().Critical();
                    if (queued != null)
                    {
                        queued.Add((QueuedMessage)(changeDb = new QueuedMessage(changeDb)));
                    }
                    RecordSent(changeDb);
                    changeDb.Write(snapshot);
                    Interlocked.Increment(ref messagesSent);
                }
                LogUsage(db);
            }
            if (next.Command == RedisLiteral.QUIT)
            {
                abort = true; // no more!
            }
            if (next.Command == RedisLiteral.SELECT)
            {
                // dealt with above; no need to send SELECT, SELECT
            }
            else
            {
                var mm = next as IMultiMessage;
                var tmp = next;
                if (queued != null)
                {
                    if (mm != null) throw new InvalidOperationException("Cannot perform composite operations (such as transactions) inside transactions");
                    queued.Add((QueuedMessage)(tmp = new QueuedMessage(tmp)));
                }

                if (mm == null)
                {
                    RecordSent(tmp);
                    tmp.Write(snapshot);
                    Interlocked.Increment(ref messagesSent);
                    switch (tmp.Command)
                    {
                        // scripts can change database
                        case RedisLiteral.EVAL:
                        case RedisLiteral.EVALSHA:
                        // transactions can be aborted without running the inner commands (SELECT) that have been written
                        case RedisLiteral.DISCARD:
                        case RedisLiteral.EXEC:
                            // we can't trust the current database; whack it
                            db = -1;
                            break;
                    }
                }
                else
                {
                    mm.Execute(this, ref db);
                }
            }
        }

        internal void WriteRaw(RedisMessage message)
        {
            if (message.Db >= 0) throw new ArgumentException("message", "WriteRaw cannot be used with db-centric messages");
            RecordSent(message);
            message.Write(outBuffer);
            Interlocked.Increment(ref messagesSent);
        }
        /// <summary>
        /// Return the number of items in the sent-queue
        /// </summary>
        protected int GetSentCount() { lock (sent) { return sent.Count; } }
        internal virtual void RecordSent(RedisMessage message, bool drainFirst = false) {
            Debug.Assert(message != null, "messages should not be null");
            lock (sent)
            {
                if (drainFirst && sent.Count != 0)
                {
                    // drain it down; the dequeuer will wake us
                    Monitor.Wait(sent);
                }
                sent.Enqueue(message);
            }
        }
        /// <summary>
        /// Indicates the current state of the connection to the server
        /// </summary>
        public enum ConnectionState
        {
            /// <summary>
            /// A connection that has not yet been innitialized
            /// </summary>
            [Obsolete("Please use New instead"), DebuggerBrowsable(DebuggerBrowsableState.Never)]
            Shiny = 0,
            /// <summary>
            /// A connection that has not yet been innitialized
            /// </summary>
            New = 0,
            /// <summary>
            /// A connection that is in the process of opening
            /// </summary>
            Opening = 1,
            /// <summary>
            /// An open connection
            /// </summary>
            Open = 2,
            /// <summary>
            /// A connection that is in the process of closing
            /// </summary>
            Closing = 3,
            /// <summary>
            /// A connection that is now closed and cannot be used
            /// </summary>
            Closed = 4
        }
        private readonly MemoryStream bodyBuffer = new MemoryStream();


        internal Task<bool> ExecuteBoolean(RedisMessage message, bool queueJump)
        {
            var msgResult = new MessageResultBoolean();
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }

        internal Task<long> ExecuteInt64(RedisMessage message, bool queueJump, object state = null)
        {
            var msgResult = new MessageResultInt64(state);
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }

        internal Task ExecuteVoid(RedisMessage message, bool queueJump, object state = null)
        {
            var msgResult = new MessageResultVoid(state);
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }

        internal Task<double> ExecuteDouble(RedisMessage message, bool queueJump)
        {
            var msgResult = new MessageResultDouble();
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }

        internal Task<byte[]> ExecuteBytes(RedisMessage message, bool queueJump)
        {
            var msgResult = new MessageResultBytes();
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }

        internal Task<RedisResult> ExecuteRaw(RedisMessage message, bool queueJump, object state = null)
        {
            var msgResult = new MessageResultRaw(state);
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }

        internal Task<string> ExecuteString(RedisMessage message, bool queueJump, object state = null)
        {
            var msgResult = new MessageResultString(state);
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }
        internal Task<long?> ExecuteNullableInt64(RedisMessage message, bool queueJump)
        {
            var msgResult = new MessageResultNullableInt64();
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }
        internal Task<double?> ExecuteNullableDouble(RedisMessage message, bool queueJump, object state = null)
        {
            var msgResult = new MessageResultNullableDouble(state);
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }
        internal Task<byte[][]> ExecuteMultiBytes(RedisMessage message, bool queueJump)
        {
            var msgResult = new MessageResultMultiBytes();
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }

        internal Task<string[]> ExecuteMultiString(RedisMessage message, bool queueJump, object state = null)
        {
            var msgResult = new MessageResultMultiString(state);
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }

        internal Task<KeyValuePair<byte[], double>[]> ExecutePairs(RedisMessage message, bool queueJump)
        {
            var msgResult = new MessageResultPairs();
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }
        internal Task<Dictionary<string, byte[]>> ExecuteHashPairs(RedisMessage message, bool queueJump)
        {
            var msgResult = new MessageResultHashPairs();
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }
        internal Task<Dictionary<string, string>> ExecuteStringPairs(RedisMessage message, bool queueJump)
        {
            var msgResult = new MessageResultStringPairs();
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }
        internal Task<KeyValuePair<string, double>[]> ExecuteStringDoublePairs(RedisMessage message, bool queueJump)
        {
            var msgResult = new MessageResultStringDoublePairs();
            message.SetMessageResult(msgResult);
            EnqueueMessage(message, queueJump);
            return msgResult.Task;
        }

        private readonly object writeLock = new object();
        private int pendingWriterCount;
        private void WritePendingQueue()
        {
            RedisMessage next;
            do
            {
                lock (unsent)
                {
                    next = unsent.Count == 0 ? null : unsent.Dequeue();
                }
                if (next != null)
                {
                    Trace("pending", "dequeued: {0}", next);
                    WriteMessage(next, true);
                }
            } while (next != null);
        }
        private void WriteMessage(RedisMessage message, bool isHigh)
        {
            if (abort && message.Command != RedisLiteral.QUIT)
            {
                CompleteMessage(message, RedisResult.Error(
                    "The system aborted before this message was sent"
#if VERBOSE
                    + ": " + message
#endif
                    ));
                return;
            }
            if (!message.ChangeState(MessageState.NotSent, MessageState.Sent))
            {
                // already cancelled; not our problem any more...
                Interlocked.Increment(ref messagesCancelled);
                return;
            }
            if (isHigh) Interlocked.Increment(ref queueJumpers);
            WriteMessage(ref db, message, null);
        }
        private volatile bool hold = true;

        internal void EnqueueMessage(RedisMessage message, bool queueJump)
        {
            bool decr = true;
            Interlocked.Increment(ref pendingWriterCount);
            try
            {
                if (message != null && !message.IsDuringInit)
                {
                    if (queueJump || hold)
                    {
                        if (abort) throw new InvalidOperationException("The connection has been closed; no new messages can be delivered");
                        lock (unsent)
                        {
                            Trace("pending", "enqueued: {0}", message);
                            unsent.Enqueue(message);
                        }
                        if (hold)
                        {
                            Interlocked.Decrement(ref pendingWriterCount);
                            return;
                        }
                    }
                }
                lock (writeLock)
                {
                    if (message == null)
                    {   // send anything buffered, and process any backlog
                        Flush(true);
                        WritePendingQueue();
                    }
                    else if (message.IsDuringInit)
                    {
                        // ONLY write that message; no queues
                        WriteMessage(message, false);
                    }
                    else if (queueJump)
                    {
                        // we enqueued to the end of the queue; just write the queue
                        WritePendingQueue();
                    }
                    else
                    {
                        // we didn't enqueue; write the queue, then our message, then queue again
                        WritePendingQueue();
                        WriteMessage(message, false);
                        WritePendingQueue();
                    }
                    bool fullFlush = Interlocked.Decrement(ref pendingWriterCount) == 0;
                    decr = false;

                    if (message == null || !message.IsDuringInit)
                    { // this excludes the case where we are just sending an init message, where we don't flush
                        Flush(fullFlush);
                    }
                }
            }
            catch
            {
                if(decr) Interlocked.Decrement(ref pendingWriterCount);
                throw;
            }
        }
        internal void CancelUnsent()
        {
            lock (unsent)
            {
                while (unsent.Count != 0)
                {
                    var next = unsent.Dequeue();
                    RedisResult result = RedisResult.Cancelled;
                    object ctx = ProcessReply(ref result, next);
                    ProcessCallbacks(ctx, result);
                }
            }
        }
        static readonly RedisMessage[] noMessages = new RedisMessage[0];
        internal RedisMessage[] DequeueAll()
        {
            lock (unsent)
            {
                int len = unsent.Count;
                if (len == 0) return noMessages;
                var arr = new RedisMessage[len];
                for (int i = 0; i < arr.Length; i++)
                    arr[i] = unsent.Dequeue();
                return arr;
            }
        }
        /// <summary>
        /// If the task is not yet completed, blocks the caller until completion up to a maximum of SyncTimeout milliseconds.
        /// Once a task is completed, the result is returned.
        /// </summary>
        /// <param name="task">The task to wait on</param>
        /// <returns>The return value of the task.</returns>
        /// <exception cref="TimeoutException">If SyncTimeout milliseconds is exceeded.</exception>
        public T Wait<T>(Task<T> task)
        {
            Wait((Task)task);
            return task.Result;
        }

        /// <summary>
        /// If true, then when using the Wait methods, information about the oldest outstanding message
        /// is included in the exception; this often points to a particular operation that was monopolising
        /// the connection
        /// </summary>
        public bool IncludeDetailInTimeouts { get; set; }

        /// <summary>
        /// If the task is not yet completed, blocks the caller until completion up to a maximum of SyncTimeout milliseconds.
        /// </summary>
        /// <param name="task">The task to wait on</param>
        /// <exception cref="TimeoutException">If SyncTimeout milliseconds is exceeded.</exception>
        /// <remarks>If an exception is throw, it is extracted from the AggregateException (unless multiple exceptions are found)</remarks>
        public void Wait(Task task)
        {
            if (task == null) throw new ArgumentNullException("task");
            try
            {
                if (!task.Wait(syncTimeout))
                {
                    throw CreateTimeout();
                }
            }
            catch (AggregateException ex)
            {
                if (ex.InnerExceptions.Count == 1)
                {
                    throw ex.InnerExceptions[0];
                }
                throw;
            }
        }

        /// <summary>
        /// Give some information about the oldest incomplete (but sent) message on the server
        /// </summary>
        protected virtual string GetTimeoutSummary()
        {
            return null;
        }
        private TimeoutException CreateTimeout()
        {
            string message = null;
            if (state != (int)ConnectionState.Open)
            {
                message = "The operation has timed out; the connection is not open";
#if VERBOSE
                message += " (" + ((ConnectionState)state).ToString() + ")";
#endif
                
            }
            else if (IncludeDetailInTimeouts)
            {
                string compete = GetTimeoutSummary();
                if (!string.IsNullOrWhiteSpace(compete))
                {
                    message = "The operation has timed out; possibly blocked by: " + compete;
                }
            }
#if VERBOSE
            if (message != null)
            {
                message += " (after " + (outBuffer == null ? -1 : outBuffer.Position) + " bytes)";
            }
#endif
            return message == null ? new TimeoutException() : new TimeoutException(message);
        }
        /// <summary>
        /// Waits for all of a set of tasks to complete, up to a maximum of SyncTimeout milliseconds.
        /// </summary>
        /// <param name="tasks">The tasks to wait on</param>
        /// <exception cref="TimeoutException">If SyncTimeout milliseconds is exceeded.</exception>
        public void WaitAll(params Task[] tasks)
        {
            if (tasks == null) throw new ArgumentNullException("tasks");
            if (!Task.WaitAll(tasks, syncTimeout))
            {
                throw CreateTimeout();
            }
        }
        /// <summary>
        /// Waits for any of a set of tasks to complete, up to a maximum of SyncTimeout milliseconds.
        /// </summary>
        /// <param name="tasks">The tasks to wait on</param>
        /// <returns>The index of a completed task</returns>
        /// <exception cref="TimeoutException">If SyncTimeout milliseconds is exceeded.</exception>        
        public int WaitAny(params Task[] tasks)
        {
            if (tasks == null) throw new ArgumentNullException("tasks");
            return Task.WaitAny(tasks, syncTimeout);
        }
        /// <summary>
        /// Add a continuation (a callback), to be executed once a task has completed
        /// </summary>
        /// <param name="task">The task to add a continuation to</param>
        /// <param name="action">The continuation to perform once completed</param>
        /// <returns>A new task representing the composed operation</returns>
        public Task ContinueWith<T>(Task<T> task, Action<Task<T>> action)
        {
            return task.ContinueWith(action, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        /// <summary>
        /// Add a continuation (a callback), to be executed once a task has completed
        /// </summary>
        /// <param name="task">The task to add a continuation to</param>
        /// <param name="action">The continuation to perform once completed</param>
        /// <returns>A new task representing the composed operation</returns>
        public Task ContinueWith(Task task, Action<Task> action)
        {
            return task.ContinueWith(action, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        /// <summary>
        /// What type of connection is this
        /// </summary>
        public ServerType ServerType { get; private set; }

    }

    /// <summary>
    /// What type of server does this represent
    /// </summary>
    public enum ServerType
    {
        /// <summary>
        /// The server is not yet connected, or is not recognised
        /// </summary>
        Unknown = 0,
        /// <summary>
        /// The server is a master node, suitable for read and write
        /// </summary>
        Master = 1,
        /// <summary>
        /// The server is a replication slave, suitable for read
        /// </summary>
        Slave = 2,
        /// <summary>
        /// The server is a sentinel, used for anutomated configuration
        /// and failover
        /// </summary>
        Sentinel = 3
    }
}

