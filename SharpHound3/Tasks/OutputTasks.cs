﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json;
using SharpHound3.JSON;
using SharpHound3.LdapWrappers;
using Group = SharpHound3.LdapWrappers.Group;

namespace SharpHound3.Tasks
{
    internal class OutputTasks
    {
        private static readonly List<string> UsedFileNames = new List<string>();
        private static readonly Lazy<JsonFileWriter> UserOutput = new Lazy<JsonFileWriter>(() => new JsonFileWriter("users"), false);
        private static readonly Lazy<JsonFileWriter> GroupOutput = new Lazy<JsonFileWriter>(() => new JsonFileWriter("groups"), false);
        private static readonly Lazy<JsonFileWriter> ComputerOutput = new Lazy<JsonFileWriter>(() => new JsonFileWriter("computers"), false);
        private static readonly Lazy<JsonFileWriter> DomainOutput = new Lazy<JsonFileWriter>(() => new JsonFileWriter("domains"), false);
        private static readonly Lazy<JsonFileWriter> GpoOutput = new Lazy<JsonFileWriter>(() => new JsonFileWriter("gpos"), false);
        private static readonly Lazy<JsonFileWriter> OuOutput = new Lazy<JsonFileWriter>(() => new JsonFileWriter("ous"), false);
        private static int _lastCount;
        private static int _currentCount;
        private static Timer _statusTimer;
        private static Stopwatch _runTimer;
        private static Task _computerStatusTask;
        private static readonly ConcurrentDictionary<string, int> ComputerStatusCount = new ConcurrentDictionary<string, int>();
        private static readonly BlockingCollection<ComputerStatus> ComputerStatusQueue = new BlockingCollection<ComputerStatus>();

        internal static void StartOutputTimer()
        {
            PrintStatus();
            _statusTimer = new Timer(Options.Instance.StatusInterval);
            _runTimer =new Stopwatch();
            _runTimer.Start();
            _statusTimer.Elapsed += (sender, e) =>
            {
                PrintStatus();
                _lastCount = _currentCount;
            };
            _statusTimer.AutoReset = true;
            _statusTimer.Start();
        }

        internal static void PrintStatus()
        {
            Console.WriteLine(
                _runTimer != null
                    ? $"Status: {_currentCount} objects finished (+{_currentCount - _lastCount} {(float) _currentCount / (_runTimer.ElapsedMilliseconds / 1000)})/s -- Using {Process.GetCurrentProcess().PrivateMemorySize64 / 1024 / 1024} MB RAM"
                    : $"Status: {_currentCount} objects finished (+{_currentCount - _lastCount}) -- Using {Process.GetCurrentProcess().PrivateMemorySize64 / 1024 / 1024} MB RAM");
        }

        internal static void WriteJsonOutput(LdapWrapper wrapper)
        {
            switch (wrapper)
            {
                case Computer computer:
                    ComputerOutput.Value.WriteObject(computer);
                    break;
                case Domain domain:
                    DomainOutput.Value.WriteObject(domain);
                    break;
                case GPO gpo:
                    GpoOutput.Value.WriteObject(gpo);
                    break;
                case Group group:
                    GroupOutput.Value.WriteObject(group);
                    break;
                case OU ou:
                    OuOutput.Value.WriteObject(ou);
                    break;
                case User user:
                    UserOutput.Value.WriteObject(user);
                    break;
            }

            _currentCount++;
        }

        internal static async Task CompleteOutput()
        {
            PrintStatus();
            Console.WriteLine($"Enumeration finished in {_runTimer.Elapsed}");
            _runTimer.Stop();
            _statusTimer.Stop();
            if (UserOutput.IsValueCreated)
                UserOutput.Value.CloseWriter();
            if (ComputerOutput.IsValueCreated)
                ComputerOutput.Value.CloseWriter();
            if (GroupOutput.IsValueCreated)
                GroupOutput.Value.CloseWriter();
            if (DomainOutput.IsValueCreated)
                DomainOutput.Value.CloseWriter();
            if (GpoOutput.IsValueCreated)
                GpoOutput.Value.CloseWriter();
            if (OuOutput.IsValueCreated)
                OuOutput.Value.CloseWriter();

            string finalName;
            var options = Options.Instance;

            if (options.NoZip || options.NoOutput)
                return;

            if (options.ZipFilename != null)
                finalName = Helpers.ResolveFileName(Options.Instance.ZipFilename, "zip", true);
            else
            {
                finalName = Helpers.ResolveFileName("BloodHound", "zip", true);
            }

            Console.WriteLine($"Compressing data to {finalName}");

            var buffer = new byte[4096];

            if (File.Exists(finalName))
            {
                Console.WriteLine("Zip File already exists, randomizing filename");
                finalName = Helpers.ResolveFileName(Path.GetRandomFileName(), "zip", true);
                Console.WriteLine($"New filename is {finalName}");
            }

            using (var zipStream = new ZipOutputStream(File.Create(finalName)))
            {
                //Set level to 9, maximum compressions
                zipStream.SetLevel(9);

                if (options.EncryptZip)
                {
                    var password = GenerateZipPassword();
                    zipStream.Password = password;
                    Console.WriteLine($"Password for Zip file is {password}. Unzip files manually to upload to interface");
                }
                else
                {
                    Console.WriteLine("You can upload this file directly to the UI");
                }

                foreach (var file in UsedFileNames)
                {
                    var entry = new ZipEntry(Path.GetFileName(file)) {DateTime = DateTime.Now};
                    zipStream.PutNextEntry(entry);

                    using (var fileStream = File.OpenRead(file))
                    {
                        int source;
                        do
                        {
                            source = await fileStream.ReadAsync(buffer, 0, buffer.Length);
                            zipStream.Write(buffer, 0, source);
                        } while (source > 0);
                    }

                    File.Delete(file);
                }

                zipStream.Finish();
            }

            Console.WriteLine("Finished compressing files. Happy graphing!");

            if (Options.Instance.DumpComputerStatus)
            {
                CompleteComputerStatusOutput();
                await _computerStatusTask;
            }
        }

        private static string GenerateZipPassword()
        {
            const string space = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";
            var builder = new StringBuilder();
            var random = new Random();
            for (var i = 0; i < 10; i++)
            {
                builder.Append(space[random.Next(space.Length)]);
            }
            return builder.ToString();
        }

        internal static void StartComputerStatusTask()
        {
            if (!Options.Instance.DumpComputerStatus)
            {
                _computerStatusTask = null;
                return;
            }

            _computerStatusTask = Task.Factory.StartNew(() =>
            {
                var fileName = Helpers.ResolveFileName("computerstatus", "csv", true);
                var count = 0;
                using (var writer = new StreamWriter(fileName, false))
                {
                    writer.WriteLine("ComputerName, Task, Status");
                    foreach (var error in ComputerStatusQueue.GetConsumingEnumerable())
                    {
                        writer.WriteLine(error.ToCsv());
                        count++;
                        if (count % 100 == 0)
                            writer.Flush();
                    }

                    writer.Flush();
                }
            }, TaskCreationOptions.LongRunning);
        }

        internal static void AddComputerStatus(ComputerStatus status)
        {
            ComputerStatusQueue.Add(status);
            var hash = $"{status.Task}-{Regex.Replace(status.Status, @"\t|\n|\r", "")}";
            ComputerStatusCount.AddOrUpdate(hash, 1, (id, count) => count + 1);
        }

        private static void CompleteComputerStatusOutput()
        {
            ComputerStatusQueue.CompleteAdding();
            Console.WriteLine();
            Console.WriteLine("-------Computer Status Count-------");
            foreach (var key in ComputerStatusCount)
            {
                Console.WriteLine($"{key.Key}: {key.Value}");
            }
            Console.WriteLine("-----------------------------------");
        }

        /// <summary>
        /// Initializes a JsonTextWriter with the initial JSON format needed for SharpHound output
        /// </summary>
        /// <param name="baseName"></param>
        /// <returns></returns>
        private class JsonFileWriter
        {
            private int Count { get; set; }
            private JsonTextWriter JsonWriter { get; }

            private readonly string _baseFileName;

            private static readonly JsonSerializer Serializer = new JsonSerializer
            {
                NullValueHandling = NullValueHandling.Include
            };

            internal JsonFileWriter(string baseFilename)
            {
                Count = 0;
                JsonWriter = CreateFile(baseFilename);
                _baseFileName = baseFilename;
            }

            internal void CloseWriter()
            {
                JsonWriter.Flush();
                JsonWriter.WriteEndArray();
                JsonWriter.WritePropertyName("meta");
                JsonWriter.WriteStartObject();
                JsonWriter.WritePropertyName("count");
                JsonWriter.WriteValue(Count);
                JsonWriter.WritePropertyName("type");
                JsonWriter.WriteValue(_baseFileName);
                JsonWriter.WritePropertyName("version");
                JsonWriter.WriteValue(3);
                JsonWriter.WriteEndObject();
                JsonWriter.Close();
            }

            internal void WriteObject(LdapWrapper json)
            {
                Serializer.Serialize(JsonWriter, json);
                Count++;
                if (Count % 100 == 0)
                    JsonWriter.Flush();
            }

            private static JsonTextWriter CreateFile(string baseName)
            {
                var filename = Helpers.ResolveFileName(baseName, "json", true);
                UsedFileNames.Add(filename);

                var exists = File.Exists(filename);
                if (exists)
                {
                    throw new FileExistsException($"File {filename} already exists. This should never happen!");
                }

                var writer = new StreamWriter(filename, false, Encoding.UTF8);
                var jsonFormat = Options.Instance.PrettyJson ? Formatting.Indented : Formatting.None;

                var jsonWriter = new JsonTextWriter(writer) { Formatting = jsonFormat };
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName(baseName);
                jsonWriter.WriteStartArray();

                return jsonWriter;
            }

        }
    }
}