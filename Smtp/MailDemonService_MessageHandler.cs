﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace MailDemon
{
    public partial class MailDemonService
    {
        private async Task ProcessConnection()
        {
            using (TcpClient tcpClient = await server.AcceptTcpClientAsync())
            {
                await HandleClientConnectionAsync(tcpClient);
            }
        }

        private async Task<string> ReadLineAsync(Stream reader)
        {
            byte[] buf = new byte[1];
            byte b;
            string result = string.Empty;
            MemoryStream ms = new MemoryStream(256);
            while (reader != null && await reader.ReadAsync(buf, 0, 1) == 1)
            {
                b = buf[0];
                switch (b)
                {
                    case (byte)'\n':
                        reader = null;
                        break;

                    case (byte)'\r':
                    case 0:
                        break;

                    default:
                        ms.WriteByte(b);
                        break;
                }
                if (ms.Length > maxLineSize)
                {
                    throw new InvalidOperationException("Line too large");
                }
            }
            result = MailDemonExtensionMethods.Utf8EncodingNoByteMarker.GetString(ms.GetBuffer().AsSpan(0, (int)ms.Length));
            MailDemonLog.Write(LogLevel.Debug, "CLIENT: " + result);
            return result;
        }

        private async Task ReadWriteAsync(Stream reader, Stream writer, int count)
        {
            byte[] buffer = new byte[8192];
            ValueTask<int> readTask;
            while (!cancelToken.IsCancellationRequested && count > 0)
            {
                readTask = reader.ReadAsync(buffer, cancelToken);
                if (!readTask.IsCompletedSuccessfully)
                {
                    break;
                }
                count -= readTask.Result;
                await writer.WriteAsync(buffer, 0, readTask.Result, cancelToken);
            }
            await writer.FlushAsync();
        }

        private async Task HandleEhlo(StreamWriter writer, SslStream sslStream, X509Certificate2 sslCertificate)
        {
            await writer.WriteLineAsync($"250-SIZE {maxMessageSize}");
            await writer.WriteLineAsync($"250-8BITMIME");
            await writer.WriteLineAsync($"250-AUTH PLAIN");
            await writer.WriteLineAsync($"250-PIPELINING");
            await writer.WriteLineAsync($"250-ENHANCEDSTATUSCODES");
            await writer.WriteLineAsync($"250-BINARYMIME");
            await writer.WriteLineAsync($"250-CHUNKING");
            if (!string.IsNullOrWhiteSpace(sslCertificateFile) && sslStream == null && port != 465 && port != 587)
            {
                await writer.WriteLineAsync($"250-STARTTLS");
            }
            await writer.WriteLineAsync($"250-SMTPUTF8");
        }

        private async Task<MailDemonUser> Authenticate(Stream reader, StreamWriter writer, string line)
        {
            MailDemonUser foundUser = null;
            if (line == "AUTH PLAIN")
            {
                await writer.WriteLineAsync($"334");
                line = await ReadLineAsync(reader) ?? string.Empty;
            }
            else
            {
                line = line.Substring(11);
            }
            string sentAuth = Encoding.UTF8.GetString(Convert.FromBase64String(line)).Replace("\0", "(null)");
            foreach (MailDemonUser user in users)
            {
                if (user.Authenticate(sentAuth))
                {
                    foundUser = user;
                    break;
                }
            }
            if (foundUser != null)
            {
                MailDemonLog.Write(LogLevel.Info, "User {0} authenticated", foundUser.Name);
                await writer.WriteLineAsync($"235 2.7.0 Accepted");
                return foundUser;
            }

            // fail
            MailDemonLog.Write(LogLevel.Warn, "Authentication failed: {0}", sentAuth);
            await writer.WriteLineAsync($"535 authentication failed");
            string userName = null;
            for (int i = 0; i < sentAuth.Length; i++)
            {
                if (sentAuth[i] == '\0')
                {
                    userName = sentAuth.Substring(0, i);
                    break;
                }
            }
            return new MailDemonUser(userName, userName, null, null, null, false);
        }

        private async Task HandleClientConnectionAsync(TcpClient tcpClient)
        {
            string ipAddress = (tcpClient.Client.RemoteEndPoint as IPEndPoint).Address.ToString();
            MailDemonUser authenticatedUser = null;
            X509Certificate2 sslCert = null;
            try
            {
                tcpClient.ReceiveTimeout = tcpClient.SendTimeout = streamTimeoutMilliseconds;

                MailDemonLog.Write(LogLevel.Info, "Connection from {0}", ipAddress);

                // immediately drop if client is blocked
                if (CheckBlocked(ipAddress))
                {
                    tcpClient.Close();
                    MailDemonLog.Write(LogLevel.Warn, "Blocking {0}", ipAddress);
                    return;
                }

                using (NetworkStream clientStream = tcpClient.GetStream())
                {
                    // create comm streams
                    SslStream sslStream = null;
                    clientStream.ReadTimeout = clientStream.WriteTimeout = streamTimeoutMilliseconds;
                    Stream reader = clientStream;
                    StreamWriter writer = new StreamWriter(clientStream, MailDemonExtensionMethods.Utf8EncodingNoByteMarker) { AutoFlush = true, NewLine = "\r\n" };

                    if (port == 465 || port == 587)
                    {
                        sslCert = (sslCert ?? LoadSslCertificate());
                        Tuple<SslStream, Stream, StreamWriter> tls = await StartTls(tcpClient, ipAddress, reader, writer, false, sslCert);
                        if (tls == null)
                        {
                            throw new IOException("Failed to start TLS, ssl certificate failed to load");
                        }
                        sslStream = tls.Item1;
                        reader = tls.Item2;
                        writer = tls.Item3;
                    }

                    MailDemonLog.Write(LogLevel.Info, "Connection accepted from {0}", ipAddress);

                    // send greeting
                    await writer.WriteLineAsync($"220 {Domain} {greeting}");

                    while (true)
                    {
                        // read initial client string
                        string line = await ReadLineAsync(reader);
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                        {
                            // empty line or QUIT terminates session
                            break;
                        }
                        else if (line.StartsWith("RSET", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync($"250 2.0.0 Resetting");
                            authenticatedUser = null;
                        }
                        else if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                        {
                            await HandleEhlo(writer, sslStream, sslCert);
                        }
                        else if (line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync($"220 {Domain} Hello {line.Substring(4).Trim()}");
                        }
                        else if (line.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
                        {
                            authenticatedUser = await Authenticate(reader, writer, line);
                            if (!authenticatedUser.Authenticated)
                            {
                                throw new InvalidOperationException("Authentication failed");
                            }
                        }
                        else if (line.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase))
                        {
                            if (sslStream != null)
                            {
                                await writer.WriteLineAsync("503 TLS already initiated");
                            }
                            else
                            {
                                sslCert = (sslCert ?? LoadSslCertificate());
                                Tuple<SslStream, Stream, StreamWriter> tls = await StartTls(tcpClient, ipAddress, reader, writer, true, sslCert);
                                if (tls == null)
                                {
                                    await writer.WriteLineAsync("503 Failed to start TLS");
                                }
                                else
                                {
                                    sslStream = tls.Item1;
                                    reader = tls.Item2;
                                    writer = tls.Item3;
                                }
                            }
                        }

                        // if authenticated, only valid line is MAIL FROM
                        // TODO: consider changing this
                        else if (authenticatedUser != null)
                        {
                            if (line.StartsWith("MAIL FROM:<", StringComparison.OrdinalIgnoreCase))
                            {
                                await SendMail(authenticatedUser, reader, writer, line);
                            }
                            else
                            {
                                MailDemonLog.Write(LogLevel.Warn, "Ignoring client command: " + line);
                            }
                        }
                        else
                        {
                            if (line.StartsWith("MAIL FROM:<", StringComparison.OrdinalIgnoreCase))
                            {
                                // non-authenticated user, forward message on if possible, check settings
                                await ReceiveMail(reader, writer, line);
                            }
                            else
                            {
                                throw new InvalidOperationException("Invalid message: " + line + ", not authenticated");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                IncrementFailure(ipAddress, authenticatedUser?.Name);
                MailDemonLog.Error(ex);
            }
            finally
            {
                sslCert?.Dispose();
                MailDemonLog.Write(LogLevel.Info, "{0} disconnected", ipAddress);
            }
        }
    }
}
