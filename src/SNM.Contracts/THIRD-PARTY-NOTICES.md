Third-party notices for SNM.Contracts
=====================================

The files under Protocol/Vendored/ are copied from the dotnet/aspnetcore repository
(https://github.com/dotnet/aspnetcore), branch release/10.0, commit
634a767d1d5a14b210f34dd38ed2f7cbfe7d1cf6 (the upstream copies were fetched by the research spike; only the namespace line and
using directives were changed), and are licensed under the MIT License:

  Licensed to the .NET Foundation under one or more agreements.
  The .NET Foundation licenses this file to you under the MIT license.

  Copyright (c) .NET Foundation and Contributors

  Permission is hereby granted, free of charge, to any person obtaining a copy of this
  software and associated documentation files (the "Software"), to deal in the Software
  without restriction, including without limitation the rights to use, copy, modify, merge,
  publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
  to whom the Software is furnished to do so, subject to the following conditions:

  The above copyright notice and this permission notice shall be included in all copies or
  substantial portions of the Software.

  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
  INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
  PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
  FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
  OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
  DEALINGS IN THE SOFTWARE.

Vendored files (upstream path -> local file; only the namespace line and using directives changed):
  src/SignalR/common/Protocols.MessagePack/src/Protocol/MessagePackHubProtocolWorker.cs -> MessagePackHubProtocolWorker.cs
  src/SignalR/common/Shared/BinaryMessageParser.cs                                        -> BinaryMessageParser.cs
  src/SignalR/common/Shared/BinaryMessageFormatter.cs                                     -> BinaryMessageFormatter.cs
  src/SignalR/common/Shared/MemoryBufferWriter.cs                                         -> MemoryBufferWriter.cs
  src/SignalR/common/Shared/TryGetReturnType.cs                                           -> ProtocolHelper.cs
