﻿/*
 * The MIT License (MIT)
 *
 * Copyright (c) 2015 Microsoft Corporation
 * 
 * -=- Robust Distributed System Nucleus (rDSN) -=- 
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */

/*
 * Description:
 *     What is this file about?
 *
 * Revision history:
 *     Feb., 2016, @imzhenyu (Zhenyu Guo), done in rDSN.CSharp project and copied here
 *     xxxx-xx-xx, author, fix bug about xxx
 */

using System;

namespace dsn.dev.csharp
{
    using dsn_task_t = IntPtr;
    using dsn_handle_t = IntPtr;
    using dsn_address_t = UInt64;

    public class Clientlet : SafeHandleZeroIsInvalid
    {
        private int _access_thread_id;
        private bool _access_thread_id_inited;
    
        // use the owner app of current thread as the host app
        public Clientlet(int task_bucket_count = 13)
            : base(IntPtr.Zero, true)
        {
            SetHandle(Native.dsn_task_tracker_create(task_bucket_count));
            _access_thread_id_inited = false;
        }

        // use explicitly specified app as the host app
        public Clientlet(string host_app_name, int host_app_index, int task_bucket_count = 13)
            : base(IntPtr.Zero, true)
        {
            SetHandle(Native.dsn_task_tracker_create(task_bucket_count));
            _access_thread_id_inited = false;
        }

        protected override bool ReleaseHandle()
        {
            if (!IsInvalid)
            {
                Native.dsn_task_tracker_destroy(handle);
                return true;
            }
            return false;
        }

        protected IntPtr tracker() { return handle; }

        public void WaitAllPendingTasks()
        {
            Native.dsn_task_tracker_wait_all(handle);
        }

        public void CancelAllPendingTasks()
        {
            Native.dsn_task_tracker_cancel_all(handle);
        }

        public static RpcAddress PrimaryAddress() 
        {
            return new RpcAddress(Native.dsn_primary_address());
        }

        public static uint random32(uint min, uint max) { return Native.dsn_random32(min, max); }
        public static ulong random64(ulong min, ulong max) { return Native.dsn_random64(min, max); }
        public static ulong now_ns() { return Native.dsn_now_ns(); }
        public static ulong now_us() { return Native.dsn_now_us(); }
        public static ulong now_ms() { return Native.dsn_now_ms(); }

        protected void CheckHashedAccess()
        {
            if (_access_thread_id_inited)
            {
                Logging.dassert((Native.dsn_threadpool_get_current_tid() == _access_thread_id),
                    "the service is assumed to be accessed by one thread only!"
                    );
            }
            else
            {
                _access_thread_id = Native.dsn_threadpool_get_current_tid();
                _access_thread_id_inited = true;
            }
        }

        public delegate void task_handler();

        // TODO: what if the task is cancelled
        static void c_task_handler(IntPtr h)
        {
            var idx2 = (int)h;
            var hr = GlobalInterOpLookupTable.GetRelease(idx2) as task_handler;
            hr();
        }

        static void c_timer_task_handler(IntPtr h)
        {
            var idx2 = (int)h;
            var hr = GlobalInterOpLookupTable.Get(idx2) as task_handler;
            hr();
        }

        static dsn_task_handler_t _c_task_handler_holder = c_task_handler;
        static dsn_task_handler_t _c_timer_task_handler_holder = c_timer_task_handler;

        public static void CallAsync(
            TaskCode evt,
            Clientlet callbackOwner,
            task_handler callback,
            int hash = 0,
            int delay_milliseconds = 0
            )
        {
            var idx = GlobalInterOpLookupTable.Put(callback);
            var task = Native.dsn_task_create(evt, _c_task_handler_holder, (IntPtr)idx, hash, callbackOwner?.tracker() ?? IntPtr.Zero);
            Native.dsn_task_call(task, delay_milliseconds);
        }

        //
        // this gives you the task handle so you can wait or cancel
        // the task, with the cost of add/ref the task handle
        // 
        public static SafeTaskHandle CallAsync2(
            TaskCode evt,
            Clientlet callbackOwner,
            task_handler callback,
            int hash = 0,
            int delay_milliseconds = 0,
            int timer_interval_milliseconds = 0
            )
        {
            var idx = GlobalInterOpLookupTable.Put(callback);

            var task = timer_interval_milliseconds == 0 ? Native.dsn_task_create(evt, _c_task_handler_holder, (IntPtr)idx, hash, callbackOwner?.tracker() ?? IntPtr.Zero) : Native.dsn_task_create_timer(evt, _c_timer_task_handler_holder, (IntPtr)idx, hash, timer_interval_milliseconds, callbackOwner?.tracker() ?? IntPtr.Zero);

            var ret = new SafeTaskHandle(task, idx);
            Native.dsn_task_call(task, delay_milliseconds);
            return ret;
        }

        // no callback
        public static void RpcCallOneWay(
            RpcAddress server,
            RpcWriteStream requestStream
            )
        {
            Logging.dassert(requestStream.IsFlushed(),
                "RpcWriteStream must be flushed after write in the same thread");

            Native.dsn_rpc_call_one_way(server.addr, requestStream.DangerousGetHandle());
        }

        public static RpcReadStream RpcCallSync(
            RpcAddress server,
            RpcWriteStream requestStream
            )
        {
            Logging.dassert(requestStream.IsFlushed(), 
                "RpcWriteStream must be flushed after write in the same thread");

            var respMsg = Native.dsn_rpc_call_wait(server.addr, requestStream.DangerousGetHandle());
            return IntPtr.Zero == respMsg ? null : new RpcReadStream(respMsg, true);
        }

        public delegate void RpcResponseHandler(ErrorCode err, RpcReadStream responseStream);

        static void c_rpc_response_handler(int err, IntPtr reqc, IntPtr respc, IntPtr h)
        {
            var idx2 = (int)h;
            var hr = GlobalInterOpLookupTable.GetRelease(idx2) as RpcResponseHandler;
            
            if (err == 0)
            {
                var rms = new RpcReadStream(respc, false);
                hr(new ErrorCode(err), rms);
            }
            else
            {
                hr(new ErrorCode(err), null);
            }
        }

        static dsn_rpc_response_handler_t _c_rpc_response_handler_holder = c_rpc_response_handler;

        public static void RpcCallAsync(
            RpcAddress server,
            RpcWriteStream requestStream,
            Clientlet callbackOwner,
            RpcResponseHandler callback,
            int replyHash = 0
            )
        {
            Logging.dassert(requestStream.IsFlushed(),
                "RpcWriteStream must be flushed after write in the same thread");

            var idx = GlobalInterOpLookupTable.Put(callback);
            var task = Native.dsn_rpc_create_response_task(
                requestStream.DangerousGetHandle(),
                _c_rpc_response_handler_holder, 
                (IntPtr)idx, 
                replyHash,
                callbackOwner?.tracker() ?? IntPtr.Zero
                );
            Native.dsn_rpc_call(server.addr, task);
        }

        //
        // this gives you the task handle so you can wait or cancel
        // the task, with the cost of add/ref the task handle
        // 
        public static SafeTaskHandle RpcCallAsync2(
            RpcAddress server,
            RpcWriteStream requestStream,
            Clientlet callbackOwner,
            RpcResponseHandler callback,
            int replyHash = 0
            )
        {
            Logging.dassert(requestStream.IsFlushed(),
                "RpcWriteStream must be flushed after write in the same thread");

            var idx = GlobalInterOpLookupTable.Put(callback);
            var task = Native.dsn_rpc_create_response_task(
                requestStream.DangerousGetHandle(),
                _c_rpc_response_handler_holder,
                (IntPtr)idx,
                replyHash,
                callbackOwner?.tracker() ?? IntPtr.Zero
                );

            var ret = new SafeTaskHandle(task, idx);
            Native.dsn_rpc_call(server.addr, task);
            return ret;
        }

        public static dsn_handle_t FileOpen(string file_name, int flag, int pmode)
        {
            return Native.dsn_file_open(file_name, flag, pmode);
        }

        public static ErrorCode FileClose(dsn_handle_t file)
        {
            var err = Native.dsn_file_close(file);
            return new ErrorCode(err);
        }

        public delegate void AioHandler(ErrorCode err, int size);

        static void c_aio_handler(int err, IntPtr size, IntPtr h)
        {
            var idx2 = (int)h;
            var hr = GlobalInterOpLookupTable.GetRelease(idx2) as AioHandler;
            
            hr(new ErrorCode(err), size.ToInt32());
        }

        static dsn_aio_handler_t _c_aio_handler_holder = c_aio_handler;

        public static SafeTaskHandle FileRead(
            dsn_handle_t hFile,
            byte[] buffer,
            int count,
            ulong offset,
            TaskCode callbackCode,
            Clientlet callbackOwner,
            AioHandler callback,
            int hash = 0
            )
        {
            var idx = GlobalInterOpLookupTable.Put(callback);
            var task = Native.dsn_file_create_aio_task(callbackCode, _c_aio_handler_holder, (IntPtr)idx, hash, callbackOwner?.tracker() ?? IntPtr.Zero);
            Native.dsn_file_read(hFile, buffer, count, offset, task);
            return new SafeTaskHandle(task, idx);
        }

        public static SafeTaskHandle FileWrite(
            dsn_handle_t hFile,
            byte[] buffer,
            int count,
            ulong offset,
            TaskCode callbackCode,
            Clientlet callbackOwner,
            AioHandler callback,
            int hash = 0
            )
        {
            var idx = GlobalInterOpLookupTable.Put(callback);
            var task = Native.dsn_file_create_aio_task(callbackCode, _c_aio_handler_holder, (IntPtr)idx, hash, callbackOwner?.tracker() ?? IntPtr.Zero);
            Native.dsn_file_write(hFile, buffer, count, offset, task);
            return new SafeTaskHandle(task, idx);
        }

        public static SafeTaskHandle CopyRemoteFiles(
            ulong remote,
            string source_dir,
            string[] files,
            string dest_dir,
            bool overwrite, 
            TaskCode callbackCode,
            Clientlet callbackOwner,
            AioHandler callback,
            int hash = 0
            )
        {
            var idx = GlobalInterOpLookupTable.Put(callback);
            var task = Native.dsn_file_create_aio_task(callbackCode, _c_aio_handler_holder, (IntPtr)idx, hash, callbackOwner?.tracker() ?? IntPtr.Zero);
            Native.dsn_file_copy_remote_files(remote, source_dir, files, dest_dir, overwrite, task);
            return new SafeTaskHandle(task, idx);
        }

        public static SafeTaskHandle CopyRemoteDirectory(
            ulong remote,
            string source_dir,
            string dest_dir,
            bool overwrite,
            TaskCode callbackCode,
            Clientlet callbackOwner,
            AioHandler callback,
            int hash = 0
            )
        {
            var idx = GlobalInterOpLookupTable.Put(callback);
            var task = Native.dsn_file_create_aio_task(callbackCode, _c_aio_handler_holder, (IntPtr)idx, hash, callbackOwner?.tracker() ?? IntPtr.Zero);
            Native.dsn_file_copy_remote_directory(remote, source_dir, dest_dir, overwrite, task);
            return new SafeTaskHandle(task, idx);
        }            
    }
}
