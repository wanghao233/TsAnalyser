﻿/*   Copyright 2016 Cinegy GmbH

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/


using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.ServiceModel;
using System.ServiceModel.Description;
using System.ServiceModel.Web;
using System.Text;
using System.Threading;
using CommandLine;
using TsAnalyser.Metrics;
using TsAnalyser.Service;
using TsDecoder.TransportStream;
using TtxDecoder.Teletext;
using static System.String;

namespace TsAnalyser
{
    // ReSharper disable once ClassNeverInstantiated.Global
    internal class Program
    {
        private static bool _receiving;
        private static bool _suppressConsoleOutput;
        private static bool _decodeTsData;
        private static bool _decodeTeletext;
        private static bool _noRtpHeaders;
        private static ushort _programNumber;
        private static string _filePath;

        private static DateTime _startTime = DateTime.UtcNow;
        private static string _logFile;
        private static bool _pendingExit;
        private static ServiceHost _serviceHost;
        private static TsAnalyserApi _tsAnalyserApi;
        private static readonly UdpClient UdpClient = new UdpClient { ExclusiveAddressUse = false };
        private static readonly object LogfileWriteLock = new object();
        private static StreamWriter _logFileStream;

        private static NetworkMetric _networkMetric = new NetworkMetric();
        private static RtpMetric _rtpMetric = new RtpMetric();
        private static List<PidMetric> _pidMetrics = new List<PidMetric>();
        private static TsDecoder.TransportStream.TsDecoder _tsDecoder;
        private static TeleTextDecoder _ttxDecoder;

        private static readonly StringBuilder ConsoleDisplay = new StringBuilder(1024);
        private static int _lastPrintedTsCount;

        static void Main(string[] args)
        {
            var options = new Options();

            Console.CancelKeyPress += Console_CancelKeyPress;

            Console.WriteLine(
                // ReSharper disable once AssignNullToNotNullAttribute
                $"Cinegy Transport Stream Monitoring and Analysis Tool (Built: {File.GetCreationTime(Assembly.GetExecutingAssembly().Location)})\n");

            try
            {
                Console.CursorVisible = false;
                Console.SetWindowSize(120, 60);
            }
            catch
            {
                Console.WriteLine("Failed to increase console size - probably screen resolution is low");
            }

            if (!Parser.Default.ParseArguments(args, options))
            {
                //ask the user interactively for an address and group
                Console.WriteLine(
                    "\nSince parameters were not passed at the start, you can now enter the two most important (or just hit enter to quit)");
                Console.Write("\nPlease enter the multicast address to listen to (e.g. 239.1.1.1): ");
                var address = Console.ReadLine();

                if (IsNullOrWhiteSpace(address)) return;

                options.MulticastAddress = address;

                Console.Write("Please enter the multicast group port (e.g. 1234): ");
                var port = Console.ReadLine();
                if (IsNullOrWhiteSpace(port))
                {
                    Console.WriteLine("Not a valid group port number - press enter to exit.");
                    Console.ReadLine();
                    return;
                }
                options.MulticastGroup = int.Parse(port);

            }

            WorkLoop(options);
        }

        private static void WorkLoop(Options options)
        {
            Console.Clear();

            if (!_receiving)
            {
                _receiving = true;
                _logFile = options.LogFile;
                _suppressConsoleOutput = options.SuppressOutput;
                _decodeTsData = options.DecodeTransportStream;
                _noRtpHeaders = options.NoRtpHeaders;
                _decodeTeletext = options.DecodeTeletext;
                _programNumber = options.ProgramNumber;
                _filePath = options.FileInput;

                if (!IsNullOrWhiteSpace(_logFile))
                {
                    PrintToConsole("Logging events to file {0}", _logFile);
                }
                LogMessage("Logging started.");

                if (options.EnableWebServices)
                {
                    var httpThreadStart = new ThreadStart(delegate
                    {
                        StartHttpService(options.ServiceUrl);
                    });

                    var httpThread = new Thread(httpThreadStart) {Priority = ThreadPriority.Normal};

                    httpThread.Start();
                }

                SetupMetricsAndDecoders();

                if (!IsNullOrEmpty(options.FileInput))
                {
                    StartStreamingFile(options.FileInput);
                }
                else
                {
                    StartListeningToNetwork(options.MulticastAddress, options.MulticastGroup, options.AdapterAddress);
                }
            }

            Console.Clear();

            while (!_pendingExit)
            {
                var runningTime = DateTime.UtcNow.Subtract(_startTime);

                if (!_suppressConsoleOutput)
                {
                    Console.SetCursorPosition(0, 0);

                    PrintToConsole("URL: rtp://@{0}:{1}\tRunning time: {2:hh\\:mm\\:ss}\t\t", options.MulticastAddress,
                        options.MulticastGroup, runningTime);

                    if (IsNullOrEmpty(_filePath))
                    {
                        PrintToConsole(
                            "\nNetwork Details\n----------------\nTotal Packets Rcvd: {0} \tBuffer Usage: {1:0.00}%\t\t\nTotal Data (MB): {2}\t\tPackets per sec:{3}",
                            _networkMetric.TotalPackets, _networkMetric.NetworkBufferUsage,
                            _networkMetric.TotalData/1048576,
                            _networkMetric.PacketsPerSecond);
                        PrintToConsole("Time Between Packets (ms): {0} \tShortest/Longest: {1}/{2}",
                            _networkMetric.TimeBetweenLastPacket, _networkMetric.ShortestTimeBetweenPackets,
                            _networkMetric.LongestTimeBetweenPackets);
                        PrintToConsole(
                            "Bitrates (Mbps): {0:0.00}/{1:0.00}/{2:0.00}/{3:0.00} (Current/Avg/Peak/Low)\t\t\t",
                            (_networkMetric.CurrentBitrate/131072.0), _networkMetric.AverageBitrate/131072.0,
                            (_networkMetric.HighestBitrate/131072.0), (_networkMetric.LowestBitrate/131072.0));

                        if (!_noRtpHeaders)
                        {
                            PrintToConsole(
                                "\nRTP Details\n----------------\nSeq Num: {0}\tMin Lost Pkts: {1}\nTimestamp: {2}\tSSRC: {3}\t",
                                _rtpMetric.LastSequenceNumber, _rtpMetric.MinLostPackets, _rtpMetric.LastTimestamp,
                                _rtpMetric.Ssrc);
                        }
                    }

                    lock (_pidMetrics)
                    {
                        PrintToConsole(_pidMetrics.Count < 10
                            ? $"\nPID Details - Unique PIDs: {_pidMetrics.Count}\n----------------"
                            : $"\nPID Details - Unique PIDs: {_pidMetrics.Count}, (10 shown by packet count)\n----------------");

                        foreach (var pidMetric in _pidMetrics.OrderByDescending(m => m.PacketCount).Take(10))
                        {
                            PrintToConsole("TS PID: {0}\tPacket Count: {1} \t\tCC Error Count: {2}\t", pidMetric.Pid,
                                pidMetric.PacketCount, pidMetric.CcErrorCount);
                        }
                    }

                    if (_tsDecoder != null)
                    {
                        lock (_tsDecoder)
                        {
                            var pmts = _tsDecoder?.ProgramMapTables.OrderBy(p => p.ProgramNumber).ToList();

                            if (pmts != null)
                            {
                                PrintToConsole(pmts.Count < 5
                                    ? $"\t\t\t\nService Information - Service Count: {pmts.Count}\n----------------\t\t\t\t"
                                    : $"\t\t\t\nService Information - Service Count: {pmts.Count}, (5 shown)\n----------------\t\t\t\t");

                                foreach (var pmtable in pmts.Take(5))
                                {
                                    var desc = _tsDecoder.GetServiceDescriptorForProgramNumber(pmtable?.ProgramNumber);
                                    if (desc != null)
                                    {
                                        PrintToConsole(
                                            $"Service {pmtable.ProgramNumber}: {desc.ServiceName.Value} ({desc.ServiceProviderName.Value}) - {desc.ServiceTypeDescription}\t\t\t"
                                            );
                                    }
                                }
                            }

                            var pmt = _tsDecoder.GetSelectedPmt(_programNumber);
                            if (pmt != null)
                            {
                                _programNumber = pmt.ProgramNumber;
                            }

                            var serviceDesc = _tsDecoder.GetServiceDescriptorForProgramNumber(pmt?.ProgramNumber);

                            PrintToConsole(serviceDesc != null
                                ? $"\t\t\t\nElements - Selected Program {serviceDesc.ServiceName} (ID:{pmt?.ProgramNumber}) (first 5 shown)\n----------------\t\t\t\t"
                                : $"\t\t\t\nElements - Selected Program Service ID {pmt?.ProgramNumber} (first 5 shown)\n----------------\t\t\t\t");

                            if (pmt?.EsStreams != null)
                            {
                                foreach (var stream in pmt?.EsStreams.Take(5))
                                {
                                    if (stream != null)
                                        PrintToConsole(
                                            "PID: {0} ({1})", stream?.ElementaryPid,
                                            DescriptorDictionaries.ShortElementaryStreamTypeDescriptions[
                                                stream.StreamType]);
                                }
                            }
                        }

                        if (_decodeTeletext)
                        {
                            PrintTeletext();
                        }
                    }

                    if (_lastPrintedTsCount != _pidMetrics.Count)
                    {
                        _lastPrintedTsCount = _pidMetrics.Count;
                        Console.Clear();
                    }
                }

                Console.WriteLine(ConsoleDisplay.ToString());
                ConsoleDisplay.Clear();

                Thread.Sleep(20);
            }

            LogMessage("Logging stopped.");
        }

        private static void PrintTeletext()
        {
            lock (_ttxDecoder.TeletextDecodedSubtitlePage)
            {
                PrintToConsole($"\nTeleText Subtitles - decoding from Service ID {_ttxDecoder.ProgramNumber}\n----------------");

                foreach (var page in _ttxDecoder.TeletextDecodedSubtitlePage.Keys)
                {
                    PrintToConsole($"Live Decoding Page {page:X}\n");

                    //some strangeness here to get around the fact we just append to console, to clear out
                    //a fixed 4 lines of space for TTX render
                    const string clearLine = "\t\t\t\t\t\t\t\t\t";
                    var ttxRender = new[] { clearLine, clearLine, clearLine, clearLine };

                    var i = 0;

                    foreach (var line in _ttxDecoder.TeletextDecodedSubtitlePage[page])
                    {
                        if (IsNullOrEmpty(line) || IsNullOrEmpty(line.Trim()) || i >= ttxRender.Length)
                            continue;

                        ttxRender[i] = $"{new string(line.Where(c => !char.IsControl(c)).ToArray())}\t\t\t";
                        i++;
                    }

                    foreach (var val in ttxRender)
                    {
                        PrintToConsole(val);
                    }
                }
            }
        }

        private static void StartListeningToNetwork(string multicastAddress, int multicastGroup,
            string listenAdapter = "")
        {

            var listenAddress = IsNullOrEmpty(listenAdapter) ? IPAddress.Any : IPAddress.Parse(listenAdapter);

            var localEp = new IPEndPoint(listenAddress, multicastGroup);

            UdpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            UdpClient.Client.ReceiveBufferSize = 1500 * 3000;
            UdpClient.ExclusiveAddressUse = false;
            UdpClient.Client.Bind(localEp);
            _networkMetric.UdpClient = UdpClient;

            var parsedMcastAddr = IPAddress.Parse(multicastAddress);
            UdpClient.JoinMulticastGroup(parsedMcastAddr, listenAddress);

            var ts = new ThreadStart(delegate
            {
                ReceivingNetworkWorkerThread(UdpClient, localEp);
            });

            var receiverThread = new Thread(ts) { Priority = ThreadPriority.Highest };

            receiverThread.Start();
        }

        private static void StartStreamingFile(string fileName)
        {
            var fs = new FileStream(fileName, FileMode.Open);

            var ts = new ThreadStart(delegate
            {
                FileStreamWorkerThread(fs);
            });

            var receiverThread = new Thread(ts) { Priority = ThreadPriority.Highest };

            receiverThread.Start();
        }

        private static void FileStreamWorkerThread(FileStream stream)
        {
            var data = new byte[188];

            while (stream?.Read(data, 0, 188)>0)
            {
                try
                {
                    var tsPackets = TsPacketFactory.GetTsPacketsFromData(data);

                    if (tsPackets == null) break;

                    AnalysePackets(tsPackets);

                }
                catch (Exception ex)
                {
                    LogMessage($@"Unhandled exception within file streamer: {ex.Message}");
                }
            }

            _pendingExit = true;
            Thread.Sleep(250);

            Console.WriteLine("Completed reading of file - hit enter to exit!");
            Console.ReadLine();
        }

        private static void ReceivingNetworkWorkerThread(UdpClient client, IPEndPoint localEp)
        {
            while (_receiving)
            {
                var data = client.Receive(ref localEp);
                if (data == null) continue;
                try
                {
                    _networkMetric.AddPacket(data);

                    if (!_noRtpHeaders)
                    {
                        _rtpMetric.AddPacket(data);
                    }

                    var tsPackets = TsPacketFactory.GetTsPacketsFromData(data);

                    if (tsPackets == null) break;
                    
                    AnalysePackets(tsPackets);
                    
                }
                catch (Exception ex)
                {
                    LogMessage($@"Unhandled exception within network receiver: {ex.Message}");
                }
            }
        }

        private static void AnalysePackets(TsPacket[] tsPackets)
        {
            lock (_pidMetrics)
            {
                foreach (var tsPacket in tsPackets)
                {
                    var currentPidMetric = _pidMetrics.FirstOrDefault(pidMetric => pidMetric.Pid == tsPacket.Pid);

                    if (currentPidMetric == null)
                    {
                        currentPidMetric = new PidMetric { Pid = tsPacket.Pid };
                        currentPidMetric.DiscontinuityDetected += currentMetric_DiscontinuityDetected;
                        currentPidMetric.TransportErrorIndicatorDetected += currentMetric_TransportErrorIndicatorDetected;
                        _pidMetrics.Add(currentPidMetric);
                    }

                    currentPidMetric.AddPacket(tsPacket);

                    if (_tsDecoder == null) continue;
                    lock (_tsDecoder)
                    {
                        _tsDecoder.AddPacket(tsPacket);

                        if (_ttxDecoder == null) continue;
                        lock (_ttxDecoder)
                        {
                            _ttxDecoder.AddPacket(_tsDecoder, tsPacket);
                        }
                    }
                }
            }
        }

        private static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            if (_pendingExit) return; //already trying to exit - allow normal behaviour on subsequent presses
            _pendingExit = true;
            e.Cancel = true;
        }

        private static void currentMetric_DiscontinuityDetected(object sender, TransportStreamEventArgs e)
        {
            LogMessage($"Discontinuity on TS PID {e.TsPid}");
        }

        private static void currentMetric_TransportErrorIndicatorDetected(object sender, TransportStreamEventArgs e)
        {
            LogMessage($"Transport Error Indicator on TS PID {e.TsPid}");
        }

        private static void RtpMetric_SequenceDiscontinuityDetected(object sender, EventArgs e)
        {
            LogMessage("Discontinuity in RTP sequence.");
        }

        private static void NetworkMetric_BufferOverflow(object sender, EventArgs e)
        {
            LogMessage("Network buffer > 99% - probably loss of data from overflow.");
        }

        private static void PrintToConsole(string message, params object[] arguments)
        {
            if (_suppressConsoleOutput) return;

            ConsoleDisplay.AppendLine(Format(message, arguments));
        }

        private static void LogMessage(string message)
        {
            ThreadPool.QueueUserWorkItem(WriteToFile, message);
        }

        private static void WriteToFile(object msg)
        {
            lock (LogfileWriteLock)
            {
                try
                {
                    if (_logFileStream == null || _logFileStream.BaseStream.CanWrite != true)
                    {
                        if (IsNullOrWhiteSpace(_logFile)) return;

                        var fs = new FileStream(_logFile, FileMode.Append, FileAccess.Write);

                        _logFileStream = new StreamWriter(fs) { AutoFlush = true };
                    }

                    _logFileStream.WriteLine($"{DateTime.Now} - {msg}");
                }
                catch (Exception)
                {
                    Debug.WriteLine("Concurrency error writing to log file...");
                    _logFileStream?.Close();
                    _logFileStream?.Dispose();
                }
            }
        }

        private static void StartHttpService(string serviceAddress)
        {
            var baseAddress = new Uri(serviceAddress);

            _serviceHost?.Close();

            _tsAnalyserApi = new TsAnalyserApi
            {
                NetworkMetric = _networkMetric,
                TsMetrics = _pidMetrics,
                RtpMetric = _rtpMetric
            };

            _tsAnalyserApi.StreamCommand += _tsAnalyserApi_StreamCommand;

            _serviceHost = new ServiceHost(_tsAnalyserApi, baseAddress);
            var webBinding = new WebHttpBinding();

            var serviceEndpoint = new ServiceEndpoint(ContractDescription.GetContract(typeof(ITsAnalyserApi)))
            {
                Binding = webBinding,
                Address = new EndpointAddress(baseAddress)
            };

            _serviceHost.AddServiceEndpoint(serviceEndpoint);

            var webBehavior = new WebHttpBehavior
            {
                AutomaticFormatSelectionEnabled = true,
                DefaultOutgoingRequestFormat = WebMessageFormat.Json,
                HelpEnabled = true
            };

            serviceEndpoint.Behaviors.Add(webBehavior);

            //TODO: Trying to disable MEX, to allow serving of index when visiting root...

            //Metadata Exchange
            //var serviceBehavior = new ServiceMetadataBehavior {HttpGetEnabled = true};
            //_serviceHost.Description.Behaviors.AddData(serviceBehavior);

            try
            {
                _serviceHost.Open();
            }
            catch (Exception ex)
            {
                var msg =
                    "Failed to start local web API for player - either something is already using the requested URL, the tool is not running as local administrator, or netsh url reservations have not been made " +
                    "to allow non-admin users to host services.\n\n" +
                    "To make a URL reservation, permitting non-admin execution, run:\n" +
                    "netsh http add urlacl http://+:8124/ user=BUILTIN\\Users\n\n" +
                    "This is the details of the exception thrown:" +
                    ex.Message +
                    "\n\nHit enter to continue without services.\n\n";

                Console.WriteLine(msg);

                Console.ReadLine();

                LogMessage(msg);
            }
        }

        private static void SetupMetricsAndDecoders()
        {
            lock (_pidMetrics)
            {
                _startTime = DateTime.UtcNow;
                _networkMetric = new NetworkMetric();
                _rtpMetric = new RtpMetric();
                _pidMetrics = new List<PidMetric>();

                if (_decodeTsData)
                {
                    _tsDecoder = new TsDecoder.TransportStream.TsDecoder();
                    _tsDecoder.TableChangeDetected += _tsDecoder_TableChangeDetected;
                }

                if (_decodeTeletext)
                {
                    _ttxDecoder = _programNumber > 1 ? new TeleTextDecoder(_programNumber) : new TeleTextDecoder();
                }

                _rtpMetric.SequenceDiscontinuityDetected += RtpMetric_SequenceDiscontinuityDetected;
                _networkMetric.BufferOverflow += NetworkMetric_BufferOverflow;
                _networkMetric.UdpClient = UdpClient;

            }
        }

        private static void _tsDecoder_TableChangeDetected(object sender, EventArgs e)
        {
            Console.Clear();
        }

        private static void _tsAnalyserApi_StreamCommand(object sender, StreamCommandEventArgs e)
        {
            switch (e.Command)
            {
                case (StreamCommandType.ResetMetrics):
                    SetupMetricsAndDecoders();

                    _tsAnalyserApi.NetworkMetric = _networkMetric;
                    _tsAnalyserApi.TsMetrics = _pidMetrics;
                    _tsAnalyserApi.RtpMetric = _rtpMetric;
                    Console.Clear();

                    break;
                case (StreamCommandType.StopStream):
                    //todo: implement
                    break;
                case (StreamCommandType.StartStream):
                    //todo: implement
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
