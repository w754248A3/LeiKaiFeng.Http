﻿using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Linq;

namespace LeiKaiFeng.Http
{
    [Serializable]
    public sealed class MHttpResponseException : Exception
    {
        public int Status { get; private set; }

        public MHttpResponseException(int status)
        {
            Status = status;
        }
    }


    [Serializable]
    public sealed class MHttpClientException : Exception
    {
        public MHttpClientException(Exception e) : base(string.Empty, e)
        {

        }
    }

    sealed class RequestAndResponsePack
    {
        readonly TaskCompletionSource<MHttpResponse> m_source = new TaskCompletionSource<MHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        public RequestAndResponsePack(CancellationToken token, Func<MHttpStream, Task> writeRequest)
        {
            Token = token;
            WriteRequest = writeRequest;
        }

        public CancellationToken Token { get; private set; }

        public Func<MHttpStream, Task> WriteRequest { get; set; }

        public Task<MHttpResponse> Task => m_source.Task;


        public void Send(Exception e)
        {
            m_source.TrySetException(e);
        }

        public void Send(MHttpResponse response)
        {
            m_source.TrySetResult(response);
        }
        
    }


    sealed class RequestAndResponse
    {
        static async Task<Stream> CreateNewConnectAsync(MHttpClientHandler handler, Socket socket, Uri uri)
        {
            await handler.ConnectCallback(socket, uri).ConfigureAwait(false);

            return await handler.AuthenticateCallback(new NetworkStream(socket, true), uri).ConfigureAwait(false);
        }

        static Task<MHttpStream> CreateNewConnectAsync(MHttpClientHandler handler, Uri uri, CancellationToken cancellationToken)
        {
            Socket socket = new Socket(handler.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            return MHttpClientHandler.TimeOutAndCancelAsync(
                CreateNewConnectAsync(handler, socket, uri),
                (stream) => new MHttpStream(socket, stream),
                socket.Close,
                handler.ConnectTimeOut,
                cancellationToken);
        }

        static async Task ReadResponseAsync(MHttpClientHandler handler, ChannelReader<RequestAndResponsePack> reader, MHttpStream stream)
        {
            try
            {
                //需要read端出现异常,task清空后才会推出
                while (true)
                {
                    var pack = await reader.ReadAsync().ConfigureAwait(false);



                    MHttpClientHandler.LinkedTimeOutAndCancel(handler.ConnectTimeOut, pack.Token, stream.Cencel, out var token, out var closeAction);

                    try
                    {
                        MHttpResponse response = await MHttpResponse.ReadAsync(stream, handler.MaxResponseContentSize).ConfigureAwait(false);

                        pack.Send(response);

                    }
                    catch(IOException e)
                    when(e.InnerException is ObjectDisposedException)
                    {
                        pack.Send(new OperationCanceledException());
                    }
                    catch(ObjectDisposedException)
                    {
                        pack.Send(new OperationCanceledException());
                    }
                    catch(Exception e)
                    {
                        pack.Send(e);
                    }
                    finally
                    {
                        closeAction();
                    }
                    

                }
            }
            catch (ChannelClosedException)
            {

            }
        }


        static async Task WriteRequestAsync(MHttpClientHandler handler, ChannelReader<RequestAndResponsePack> reader, ChannelWriter<RequestAndResponsePack> writer, MHttpStream stream)
        {
            try
            {
                foreach (var item in Enumerable.Range(0, handler.MaxStreamRequestCount))
                {
                    RequestAndResponsePack pack;

                    using (var cancel = new CancellationTokenSource(handler.MaxStreamWaitTimeSpan))
                    {
                        pack = await reader.ReadAsync(cancel.Token).ConfigureAwait(false);
                    }

                    try
                    {
                        await pack.WriteRequest(stream).ConfigureAwait(false);

                    }
                    catch(Exception e)
                    {
                        pack.Send(e);

                        return;
                    }

                    await writer.WriteAsync(pack).ConfigureAwait(false);
                }
            }
            catch (ChannelClosedException)
            {
                
            }
            catch (OperationCanceledException)
            {
                
            }
            finally
            {
                writer.TryComplete();
            }
        }


        static Task AddTask2(MHttpClientHandler handler, ChannelReader<RequestAndResponsePack> reader, MHttpStream stream)
        {
            var channel = Channel.CreateBounded<RequestAndResponsePack>(handler.MaxStreamParallelRequestCount);


            var read_task = ReadResponseAsync(handler, channel, stream);

            var write_task = WriteRequestAsync(handler, reader, channel, stream);


            return Task.WhenAll(read_task, write_task);
        }

        static async Task AddTask(MHttpClientHandler handler, Uri uri, ChannelReader<RequestAndResponsePack> reader)
        {
            
            async Task<MHttpStream> createStream()
            {
                while (true)
                {
                    bool b = await reader.WaitToReadAsync().ConfigureAwait(false);

                    if (b == false) 
                    {
                        throw new ChannelClosedException();
                    }

                    Exception exception;
                    try
                    {
                        return await CreateNewConnectAsync(handler, uri, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        exception = e;
                    }

                    var pack = await reader.ReadAsync().ConfigureAwait(false);

                    pack.Send(exception);

                }
            }

            try
            {
                SemaphoreSlim slim = new SemaphoreSlim(handler.MaxStreamPoolCount, handler.MaxStreamPoolCount);
              
                while (true)
                {

                    await slim.WaitAsync().ConfigureAwait(false);

                    MHttpStream stream = await createStream().ConfigureAwait(false);

                    Task task = Task.Run(() => AddTask2(handler, reader, stream))
                        .ContinueWith((t) => {

                            slim.Release();

                            stream.Close();


                        });

                }

            }
            catch (ChannelClosedException)
            {

            }

        }

        public static ChannelWriter<RequestAndResponsePack> Create(MHttpClientHandler handler, Uri uri)
        {
            var channel = Channel.CreateBounded<RequestAndResponsePack>(handler.MaxStreamPoolCount);

            Task.Run(() => AddTask(handler, uri, channel));


            return channel;

        }
    }

    public sealed class MHttpClient
    {
      


        readonly StreamPool m_pool;

        readonly MHttpClientHandler m_handler;


        

        public MHttpClient() : this(new MHttpClientHandler())
        {

        }


        public MHttpClient(MHttpClientHandler handler)
        {
            m_handler = handler;

            m_pool = new StreamPool();

            
        }

        


        async Task<T> Internal_SendAsync<T>(Uri uri, MHttpRequest request, CancellationToken token, Func<MHttpResponse, T> translateFunc)
        {
            
            try
            {

                var writer = m_pool.Find(m_handler, uri);


               

                while (true)
                {
                    try
                    {
                        var pack = new RequestAndResponsePack(token, request.CreateSendAsync());

                        await writer.WriteAsync(pack).ConfigureAwait(false);

                        var response = await pack.Task.ConfigureAwait(false);

                        return translateFunc(response);
                    }
                    catch (IOException)
                    {

                    }
                    catch (ObjectDisposedException)
                    {

                    }


                }

            }
            catch(Exception e)
            {
                throw new MHttpClientException(e);
            }     
        }

        public Task<MHttpResponse> SendAsync(Uri uri, MHttpRequest request, CancellationToken cancellationToken)
        {
            return Internal_SendAsync(uri, request, cancellationToken, (res) => res);
        }


        static void ChuckResponseStatus(MHttpResponse response)
        {
            int n = response.Status;

            if (n >= 200 && n < 300)
            {

            }
            else
            {
                throw new MHttpResponseException(n);
            }
        }

        public Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
        {
            MHttpRequest request = MHttpRequest.CreateGet(uri);

            return Internal_SendAsync(uri, request, cancellationToken, (response) =>
            {
                ChuckResponseStatus(response);


                return response.Content.GetString();
            });
            
        }

        public Task<byte[]> GetByteArrayAsync(Uri uri, Uri referer, CancellationToken cancellationToken)
        {

            MHttpRequest request = MHttpRequest.CreateGet(uri);

            request.Headers.Set("Referer", referer.AbsoluteUri);

            return Internal_SendAsync(uri, request, cancellationToken, (response) =>
            {



                ChuckResponseStatus(response);

                return response.Content.GetByteArray();
            });

        }

        public Task<byte[]> GetByteArrayAsync(Uri uri, CancellationToken cancellationToken)
        {
            MHttpRequest request = MHttpRequest.CreateGet(uri);

            return Internal_SendAsync(uri, request, cancellationToken, (response) =>
            {
                ChuckResponseStatus(response);


                return response.Content.GetByteArray();
            });

        }


    }
}