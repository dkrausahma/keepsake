using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Keepsake.Core;
using Keepsake.Core.Util;

namespace Keepsake.Protocol
{
    /// <summary>Length-prefixed JSON frames: uint32 little-endian length, then UTF-8 JSON.</summary>
    public static class Wire
    {
        public const string DefaultPipeName = "keepsake-service";
        public const int MaxMessage = 64 * 1024 * 1024;

        public static void Write(Stream stream, byte[] payload)
        {
            var header = BitConverter.GetBytes((uint)payload.Length);
            stream.Write(header, 0, 4);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        /// <summary>Returns null when the other side closed the connection.</summary>
        /// <summary>Largest request the service accepts (responses, e.g. full run logs, may be larger).</summary>
        public const int MaxRequest = 8 * 1024 * 1024;

        public static byte[] Read(Stream stream) => Read(stream, MaxMessage);

        public static byte[] Read(Stream stream, int maxLength)
        {
            var header = new byte[4];
            if (!ReadExactly(stream, header, 4))
                return null;
            uint length = BitConverter.ToUInt32(header, 0);
            if (length > maxLength)
                throw new IOException("Message too large (" + length + " bytes).");
            var payload = new byte[length];
            if (!ReadExactly(stream, payload, (int)length))
                throw new IOException("Connection closed in the middle of a message.");
            return payload;
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n == 0)
                {
                    if (read == 0)
                        return false;
                    throw new IOException("Connection closed in the middle of a message.");
                }
                read += n;
            }
            return true;
        }
    }

    public sealed class ApiException : KeepsakeException
    {
        public ApiException(string code, string message) : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>Client for the Keepsake service API (used by the GUI and the CLI).</summary>
    public sealed class KeepsakeClient : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly object _lock = new object();
        private int _nextId;

        private KeepsakeClient(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
        }

        public string Token { get; set; }

        public static KeepsakeClient Connect(string pipeName = null, int timeoutMs = 3000)
        {
            // Identification only: the server may learn who the client is, but can never act as the client (SEC-005).
            var pipe = new NamedPipeClientStream(".", pipeName ?? Wire.DefaultPipeName, PipeDirection.InOut, PipeOptions.None,
                System.Security.Principal.TokenImpersonationLevel.Identification);
            try
            {
                pipe.Connect(timeoutMs);
            }
            catch (TimeoutException)
            {
                pipe.Dispose();
                throw new KeepsakeException("The Keepsake service is not running (or not reachable). Start the 'Keepsake Backup Service'.");
            }
            catch (UnauthorizedAccessException)
            {
                pipe.Dispose();
                throw new KeepsakeException("Access to the Keepsake service was denied. Run as administrator.");
            }
            try
            {
                PipeServerIdentity.Verify(pipe);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
            return new KeepsakeClient(pipe);
        }

        public T Call<T>(string method, object parameters = null)
        {
            var result = Send(method, parameters);
            if (result == null || result.Value.ValueKind == JsonValueKind.Null)
                return default(T);
            return result.Value.Deserialize<T>(Json.Compact);
        }

        public void Call(string method, object parameters = null) => Send(method, parameters);

        public string Login(string password)
        {
            var result = Call<LoginResult>(Methods.AuthLogin, new PasswordParams { Password = password });
            Token = result.Token;
            return Token;
        }

        private JsonElement? Send(string method, object parameters)
        {
            lock (_lock)
            {
                var request = new Request
                {
                    Id = ++_nextId,
                    Method = method,
                    Token = Token,
                    Params = parameters == null ? (JsonElement?)null : JsonSerializer.SerializeToElement(parameters, parameters.GetType(), Json.Compact),
                };
                try
                {
                    Wire.Write(_pipe, Json.ToBytes(request));
                    byte[] raw = Wire.Read(_pipe) ?? throw new KeepsakeException("The Keepsake service closed the connection.");
                    var response = Json.FromBytes<Response>(raw);
                    if (!response.Ok)
                        throw new ApiException(response.Error?.Code ?? ErrorCodes.Failed, response.Error?.Message ?? "Unknown error.");
                    return response.Result;
                }
                catch (IOException ex)
                {
                    throw new KeepsakeException("Lost connection to the Keepsake service: " + ex.Message);
                }
            }
        }

        /// <summary>End the session when the client is disposed (one-shot clients such as the CLI, SEC-014).</summary>
        public bool LogoutOnDispose { get; set; }

        public void Dispose()
        {
            if (LogoutOnDispose && Token != null)
            {
                try
                {
                    Call(Methods.AuthLogout);
                }
                catch (Exception ex) when (ex is KeepsakeException || ex is IOException || ex is ObjectDisposedException)
                {
                    // Best effort: the session expires anyway.
                }
            }
            _pipe.Dispose();
        }
    }
}
