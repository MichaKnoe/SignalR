// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.SignalR.Internal.Formatters;
using Microsoft.Extensions.Options;
using MsgPack;
using MsgPack.Serialization;

namespace Microsoft.AspNetCore.SignalR.Internal.Protocol
{
    public class MessagePackHubProtocol : IHubProtocol
    {
        private const int ErrorResult = 1;
        private const int VoidResult = 2;
        private const int NonVoidResult = 3;

        public static readonly string ProtocolName = "messagepack";

        public SerializationContext SerializationContext { get; }

        public string Name => ProtocolName;

        public ProtocolType Type => ProtocolType.Binary;

        public MessagePackHubProtocol()
            : this(Options.Create(new MessagePackHubProtocolOptions()))
        { }

        public MessagePackHubProtocol(IOptions<MessagePackHubProtocolOptions> options)
        {
            SerializationContext = options.Value.SerializationContext;
        }

        public bool TryParseMessages(ReadOnlySpan<byte> input, IInvocationBinder binder, out IList<HubMessage> messages)
        {
            messages = new List<HubMessage>();

            while (BinaryMessageParser.TryParseMessage(ref input, out var payload))
            {
                using (var memoryStream = new MemoryStream(payload.ToArray()))
                {
                    messages.Add(ParseMessage(memoryStream, binder));
                }
            }

            return messages.Count > 0;
        }

        private static HubMessage ParseMessage(Stream input, IInvocationBinder binder)
        {
            using (var unpacker = Unpacker.Create(input))
            {
                _ = ReadArrayLength(unpacker, "elementCount");

                // Read headers
                var headers = ReadHeaders(unpacker);

                var messageType = ReadInt32(unpacker, "messageType");

                switch (messageType)
                {
                    case HubProtocolConstants.InvocationMessageType:
                        return CreateInvocationMessage(headers, unpacker, binder);
                    case HubProtocolConstants.StreamInvocationMessageType:
                        return CreateStreamInvocationMessage(headers, unpacker, binder);
                    case HubProtocolConstants.StreamItemMessageType:
                        return CreateStreamItemMessage(headers, unpacker, binder);
                    case HubProtocolConstants.CompletionMessageType:
                        return CreateCompletionMessage(headers, unpacker, binder);
                    case HubProtocolConstants.CancelInvocationMessageType:
                        return CreateCancelInvocationMessage(headers, unpacker);
                    case HubProtocolConstants.PingMessageType:
                        return PingMessage.Instance;
                    default:
                        throw new FormatException($"Invalid message type: {messageType}.");
                }
            }
        }

        private static InvocationMessage CreateInvocationMessage(IReadOnlyDictionary<string, string> headers, Unpacker unpacker, IInvocationBinder binder)
        {
            var invocationId = ReadInvocationId(unpacker);

            // For MsgPack, we represent an empty invocation ID as an empty string,
            // so we need to normalize that to "null", which is what indicates a non-blocking invocation.
            if (string.IsNullOrEmpty(invocationId))
            {
                invocationId = null;
            }

            var target = ReadString(unpacker, "target");
            var parameterTypes = binder.GetParameterTypes(target);

            try
            {
                var arguments = BindArguments(unpacker, parameterTypes);
                return new InvocationMessage(headers, invocationId, target, argumentBindingException: null, arguments: arguments);
            }
            catch (Exception ex)
            {
                return new InvocationMessage(headers, invocationId, target, ExceptionDispatchInfo.Capture(ex));
            }
        }

        private static StreamInvocationMessage CreateStreamInvocationMessage(IReadOnlyDictionary<string, string> headers, Unpacker unpacker, IInvocationBinder binder)
        {
            var invocationId = ReadInvocationId(unpacker);
            var target = ReadString(unpacker, "target");
            var parameterTypes = binder.GetParameterTypes(target);
            try
            {
                var arguments = BindArguments(unpacker, parameterTypes);
                return new StreamInvocationMessage(headers, invocationId, target, argumentBindingException: null, arguments: arguments);
            }
            catch (Exception ex)
            {
                return new StreamInvocationMessage(headers, invocationId, target, ExceptionDispatchInfo.Capture(ex));
            }
        }

        private static StreamItemMessage CreateStreamItemMessage(IReadOnlyDictionary<string, string> headers, Unpacker unpacker, IInvocationBinder binder)
        {
            var invocationId = ReadInvocationId(unpacker);
            var itemType = binder.GetReturnType(invocationId);
            var value = DeserializeObject(unpacker, itemType, "item");
            return new StreamItemMessage(headers, invocationId, value);
        }

        private static CompletionMessage CreateCompletionMessage(IReadOnlyDictionary<string, string> headers, Unpacker unpacker, IInvocationBinder binder)
        {
            var invocationId = ReadInvocationId(unpacker);
            var resultKind = ReadInt32(unpacker, "resultKind");

            string error = null;
            object result = null;
            var hasResult = false;

            switch (resultKind)
            {
                case ErrorResult:
                    error = ReadString(unpacker, "error");
                    break;
                case NonVoidResult:
                    var itemType = binder.GetReturnType(invocationId);
                    result = DeserializeObject(unpacker, itemType, "argument");
                    hasResult = true;
                    break;
                case VoidResult:
                    hasResult = false;
                    break;
                default:
                    throw new FormatException("Invalid invocation result kind.");
            }

            return new CompletionMessage(headers, invocationId, error, result, hasResult);
        }

        private static CancelInvocationMessage CreateCancelInvocationMessage(IReadOnlyDictionary<string, string> headers, Unpacker unpacker)
        {
            var invocationId = ReadInvocationId(unpacker);
            return new CancelInvocationMessage(headers, invocationId);
        }

        private static IReadOnlyDictionary<string, string> ReadHeaders(Unpacker unpacker)
        {
            var headerCount = ReadMapLength(unpacker, "headers");
            if (headerCount > 0)
            {
                // If headerCount is larger than int.MaxValue, things are going to go horribly wrong anyway :)
                var headers = new Dictionary<string, string>((int)headerCount);

                for (var i = 0; i < headerCount; i += 1)
                {
                    var key = ReadString(unpacker, $"headers[{i}].Key");
                    var value = ReadString(unpacker, $"headers[{i}].Value");
                    headers[key] = value;
                }
                return headers;
            }
            else
            {
                return HubMessage.EmptyHeaders;
            }
        }

        private static object[] BindArguments(Unpacker unpacker, Type[] parameterTypes)
        {
            var argumentCount = ReadArrayLength(unpacker, "arguments");

            if (parameterTypes.Length != argumentCount)
            {
                throw new FormatException(
                    $"Invocation provides {argumentCount} argument(s) but target expects {parameterTypes.Length}.");
            }

            try
            {
                var arguments = new object[argumentCount];
                for (var i = 0; i < argumentCount; i++)
                {
                    arguments[i] = DeserializeObject(unpacker, parameterTypes[i], "argument");
                }

                return arguments;
            }
            catch (Exception ex)
            {
                throw new FormatException("Error binding arguments. Make sure that the types of the provided values match the types of the hub method being invoked.", ex);
            }
        }

        public void WriteMessage(HubMessage message, Stream output)
        {
            using (var memoryStream = new MemoryStream())
            {
                WriteMessageCore(message, memoryStream);
                BinaryMessageFormatter.WriteMessage(new ReadOnlySpan<byte>(memoryStream.ToArray()), output);
            }
        }

        private void WriteMessageCore(HubMessage message, Stream output)
        {
            // PackerCompatibilityOptions.None prevents from serializing byte[] as strings
            // and allows extended objects
            var packer = Packer.Create(output, PackerCompatibilityOptions.None);
            switch (message)
            {
                case InvocationMessage invocationMessage:
                    WriteInvocationMessage(invocationMessage, packer);
                    break;
                case StreamInvocationMessage streamInvocationMessage:
                    WriteStreamInvocationMessage(streamInvocationMessage, packer);
                    break;
                case StreamItemMessage streamItemMessage:
                    WriteStreamingItemMessage(streamItemMessage, packer);
                    break;
                case CompletionMessage completionMessage:
                    WriteCompletionMessage(completionMessage, packer);
                    break;
                case CancelInvocationMessage cancelInvocationMessage:
                    WriteCancelInvocationMessage(cancelInvocationMessage, packer);
                    break;
                case PingMessage pingMessage:
                    WritePingMessage(pingMessage, packer);
                    break;
                default:
                    throw new FormatException($"Unexpected message type: {message.GetType().Name}");
            }
        }

        private void WriteInvocationMessage(InvocationMessage message, Packer packer)
        {
            packer.PackArrayHeader(5);
            PackHeaders(packer, message.Headers);
            packer.Pack(HubProtocolConstants.InvocationMessageType);
            if (string.IsNullOrEmpty(message.InvocationId))
            {
                packer.PackNull();
            }
            else
            {
                packer.PackString(message.InvocationId);
            }
            packer.PackString(message.Target);
            packer.PackObject(message.Arguments, SerializationContext);
        }

        private void WriteStreamInvocationMessage(StreamInvocationMessage message, Packer packer)
        {
            packer.PackArrayHeader(5);
            PackHeaders(packer, message.Headers);
            packer.Pack(HubProtocolConstants.StreamInvocationMessageType);
            packer.PackString(message.InvocationId);
            packer.PackString(message.Target);
            packer.PackObject(message.Arguments, SerializationContext);
        }

        private void WriteStreamingItemMessage(StreamItemMessage message, Packer packer)
        {
            packer.PackArrayHeader(4);
            PackHeaders(packer, message.Headers);
            packer.Pack(HubProtocolConstants.StreamItemMessageType);
            packer.PackString(message.InvocationId);
            packer.PackObject(message.Item, SerializationContext);
        }

        private void WriteCompletionMessage(CompletionMessage message, Packer packer)
        {
            var resultKind =
                message.Error != null ? ErrorResult :
                message.HasResult ? NonVoidResult :
                VoidResult;

            packer.PackArrayHeader(4 + (resultKind != VoidResult ? 1 : 0));
            PackHeaders(packer, message.Headers);
            packer.Pack(HubProtocolConstants.CompletionMessageType);
            packer.PackString(message.InvocationId);
            packer.Pack(resultKind);
            switch (resultKind)
            {
                case ErrorResult:
                    packer.PackString(message.Error);
                    break;
                case NonVoidResult:
                    packer.PackObject(message.Result, SerializationContext);
                    break;
            }
        }

        private void WriteCancelInvocationMessage(CancelInvocationMessage message, Packer packer)
        {
            packer.PackArrayHeader(3);
            PackHeaders(packer, message.Headers);
            packer.Pack(HubProtocolConstants.CancelInvocationMessageType);
            packer.PackString(message.InvocationId);
        }

        private void WritePingMessage(PingMessage pingMessage, Packer packer)
        {
            packer.PackArrayHeader(2);

            // Pack empty headers map for Ping Message
            // We don't support headers for Ping messages, but for consistency, an empty headers map must be written out
            packer.PackMapHeader(0);

            packer.Pack(HubProtocolConstants.PingMessageType);
        }

        private void PackHeaders(Packer packer, IReadOnlyDictionary<string, string> headers)
        {
            if (headers != null)
            {
                packer.PackMapHeader(headers.Count);
                if (headers.Count > 0)
                {
                    foreach (var header in headers)
                    {
                        packer.PackString(header.Key);
                        packer.PackString(header.Value);
                    }
                }
            }
            else
            {
                packer.PackMapHeader(0);
            }
        }

        private static string ReadInvocationId(Unpacker unpacker)
        {
            return ReadString(unpacker, "invocationId");
        }

        private static int ReadInt32(Unpacker unpacker, string field)
        {
            Exception msgPackException = null;
            try
            {
                if (unpacker.ReadInt32(out var value))
                {
                    return value;
                }
            }
            catch (Exception e)
            {
                msgPackException = e;
            }

            throw new FormatException($"Reading '{field}' as Int32 failed.", msgPackException);
        }

        private static string ReadString(Unpacker unpacker, string field)
        {
            Exception msgPackException = null;
            try
            {
                if (unpacker.Read())
                {
                    if (unpacker.LastReadData.IsNil)
                    {
                        return null;
                    }
                    else
                    {
                        return unpacker.LastReadData.AsString();
                    }
                }
            }
            catch (Exception e)
            {
                msgPackException = e;
            }

            throw new FormatException($"Reading '{field}' as String failed.", msgPackException);
        }

        private static bool ReadBoolean(Unpacker unpacker, string field)
        {
            Exception msgPackException = null;
            try
            {
                if (unpacker.ReadBoolean(out var value))
                {
                    return value;
                }
            }
            catch (Exception e)
            {
                msgPackException = e;
            }

            throw new FormatException($"Reading '{field}' as Boolean failed.", msgPackException);
        }

        private static long ReadMapLength(Unpacker unpacker, string field)
        {
            Exception msgPackException = null;
            try
            {
                if (unpacker.ReadMapLength(out var value))
                {
                    return value;
                }
            }
            catch (Exception e)
            {
                msgPackException = e;
            }

            throw new FormatException($"Reading map length for '{field}' failed.", msgPackException);
        }

        private static long ReadArrayLength(Unpacker unpacker, string field)
        {
            Exception msgPackException = null;
            try
            {
                if (unpacker.ReadArrayLength(out var value))
                {
                    return value;
                }
            }
            catch (Exception e)
            {
                msgPackException = e;
            }

            throw new FormatException($"Reading array length for '{field}' failed.", msgPackException);
        }

        private static object DeserializeObject(Unpacker unpacker, Type type, string field)
        {
            Exception msgPackException = null;
            try
            {
                if (unpacker.Read())
                {
                    var serializer = MessagePackSerializer.Get(type);
                    return serializer.UnpackFrom(unpacker);
                }
            }
            catch (Exception ex)
            {
                msgPackException = ex;
            }

            throw new FormatException($"Deserializing object of the `{type.Name}` type for '{field}' failed.", msgPackException);
        }

        internal static SerializationContext CreateDefaultSerializationContext()
        {
            // serializes objects (here: arguments and results) as maps so that property names are preserved
            var serializationContext = new SerializationContext { SerializationMethod = SerializationMethod.Map };

            // allows for serializing objects that cannot be deserialized due to the lack of the default ctor etc.
            serializationContext.CompatibilityOptions.AllowAsymmetricSerializer = true;
            return serializationContext;
        }
    }
}
