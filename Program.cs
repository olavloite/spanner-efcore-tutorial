// Copyright 2021 Google Inc. All Rights Reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Api.Gax;
using Google.Api.Gax.ResourceNames;
using Google.Cloud.Spanner.Admin.Database.V1;
using Google.Cloud.Spanner.Admin.Instance.V1;
using Google.Cloud.Spanner.Common.V1;
using Google.Cloud.Spanner.Data;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace SpannerEFCoreTutorial
{
    /// <summary>
    /// A simple Console application showing how to use Entity Framework Core
    /// with Google Cloud Spanner. The application automatically starts a
    /// Cloud Spanner emulator and creates the test database on the emulator.
    /// No manual setup is required.
    /// </summary>
    public static class Program
    {
        static readonly string s_projectId = "sample-project";
        static readonly string s_instanceId = "sample-instance";
        static readonly string s_databaseId = "sample-database";

        static void Main(string[] args)
        {
            RunSampleApp().WaitWithUnwrappedExceptions();
        }

        internal static async Task RunSampleApp()
        {
            // Set the SPANNER_EMULATOR_HOST environment variable for this process. This
            // ensures that the Entity Framework provider will connect to the emulator
            // instead of to the real Google Cloud Spanner. Remove this line if you want
            // to test the application against a real Spanner database.
            Environment.SetEnvironmentVariable("SPANNER_EMULATOR_HOST", "localhost:9010");
            var emulatorRunner = new EmulatorRunner();
            try
            {
                // This starts an in-mem emulator and creates the sample database.
                await SetupAsync(emulatorRunner);

                // Create the connection string that will be used.
                DatabaseName databaseName = DatabaseName.FromProjectInstanceDatabase(s_projectId, s_instanceId, s_databaseId);
                var dataSource = $"Data Source={databaseName}";
                var connectionString = new SpannerConnectionStringBuilder(dataSource)
                {
                    EmulatorDetection = EmulatorDetection.EmulatorOrProduction,
                }.ConnectionString;
                Console.WriteLine($"Connecting to database {connectionString}");

                // Create a DbContext that uses our sample Spanner database.
                using var context = new SpannerTutorialContext(connectionString);
                var singer = new Singer
                {
                    SingerId = Guid.NewGuid(),
                    FirstName = "Bob",
                    LastName = "Allison",
                };
                context.Singers.Add(singer);
                var album = new Album
                {
                    AlbumId = Guid.NewGuid(),
                    Title = "Let's Go",
                    Singer = singer,
                };
                context.Albums.Add(album);
                var track = new Track
                {
                    Album = album,
                    TrackId = 1L,
                    Title = "Go, Go, Go",
                };
                context.Tracks.Add(track);

                // This saves all the above changes in one transaction.
                Console.WriteLine($"Writing Singer, Album and Track to the database");
                var count = await context.SaveChangesAsync();
                Console.WriteLine($"{count} records written to the database\n");

                // Get a single entity. Note that the primary key of Track consists
                // of both the parent AlbumId and the Id of the Track.
                var foundTrack = await context.Tracks.FindAsync(album.AlbumId, 1L);
                Console.WriteLine($"Found track {track.Title}");
                // You can use LINQ to query the data that was written.
                var singers = await context.Singers
                    .Where(s => s.FullName == "Bob Allison")
                    .ToListAsync();
                Console.WriteLine($"Found {singers.Count} singer(s) with full name {singers.First().LastName}");
            }
            finally
            {
                Console.WriteLine("");
                Console.WriteLine("Stopping emulator...");
                emulatorRunner.StopEmulator().WaitWithUnwrappedExceptions();
                Console.WriteLine("");
            }
        }

        private static async Task SetupAsync(EmulatorRunner emulatorRunner)
        {
            Console.WriteLine("");
            Console.WriteLine("Starting emulator...");
            emulatorRunner.StartEmulator().WaitWithUnwrappedExceptions();
            Console.WriteLine("");

            DatabaseName databaseName = DatabaseName.FromProjectInstanceDatabase(s_projectId, s_instanceId, s_databaseId);
            await MaybeCreateInstanceOnEmulatorAsync(databaseName.ProjectId, databaseName.InstanceId);
            await MaybeCreateDatabaseOnEmulatorAsync(databaseName);
        }

        private static async Task MaybeCreateInstanceOnEmulatorAsync(string projectId, string instanceId)
        {
            // Try to create an instance on the emulator and ignore any AlreadyExists error.
            var adminClientBuilder = new InstanceAdminClientBuilder
            {
                EmulatorDetection = EmulatorDetection.EmulatorOrProduction
            };
            var instanceAdminClient = await adminClientBuilder.BuildAsync();

            var instanceName = InstanceName.FromProjectInstance(projectId, instanceId);
            try
            {
                await instanceAdminClient.CreateInstance(new CreateInstanceRequest
                {
                    InstanceId = instanceName.InstanceId,
                    ParentAsProjectName = ProjectName.FromProject(projectId),
                    Instance = new Instance
                    {
                        InstanceName = instanceName,
                        ConfigAsInstanceConfigName = new InstanceConfigName(projectId, "emulator-config"),
                        DisplayName = "Sample Instance",
                        NodeCount = 1,
                    },
                }).PollUntilCompletedAsync();
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.AlreadyExists)
            {
                // Ignore
            }
        }

        private static async Task MaybeCreateDatabaseOnEmulatorAsync(DatabaseName databaseName)
        {
            // Try to create a database on the emulator and ignore any AlreadyExists error.
            var adminClientBuilder = new DatabaseAdminClientBuilder
            {
                EmulatorDetection = EmulatorDetection.EmulatorOrProduction
            };
            var databaseAdminClient = await adminClientBuilder.BuildAsync();

            var instanceName = InstanceName.FromProjectInstance(databaseName.ProjectId, databaseName.InstanceId);
            try
            {
                await databaseAdminClient.CreateDatabase(new CreateDatabaseRequest
                {
                    ParentAsInstanceName = instanceName,
                    CreateStatement = $"CREATE DATABASE `{databaseName.DatabaseId}`",
                }).PollUntilCompletedAsync();
                var connectionStringBuilder = new SpannerConnectionStringBuilder($"Data Source={databaseName}")
                {
                    EmulatorDetection = EmulatorDetection.EmulatorOnly,
                };
                await CreateSampleDataModel(connectionStringBuilder.ConnectionString);
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.AlreadyExists)
            {
                // Ignore
            }
        }

        private static async Task CreateSampleDataModel(string connectionString)
        {
            var codeBaseUrl = new Uri(Assembly.GetExecutingAssembly().CodeBase);
            var codeBasePath = Uri.UnescapeDataString(codeBaseUrl.AbsolutePath);
            var dirPath = Path.GetDirectoryName(codeBasePath);
            var fileName = Path.Combine(dirPath, "DataModel.sql");
            var script = await File.ReadAllTextAsync(fileName);
            var statements = script.Split(";");
            for (var i = 0; i < statements.Length; i++)
            {
                statements[i] = statements[i].Trim(new char[] { '\r', '\n' });
            }
            int length = statements.Length;
            if (statements[length - 1] == "")
            {
                length--;
            }
            await ExecuteDdlAsync(connectionString, statements, length);
        }

        private static async Task ExecuteDdlAsync(string connectionString, string[] ddl, int length)
        {
            string[] extraStatements = new string[length - 1];
            Array.Copy(ddl, 1, extraStatements, 0, extraStatements.Length);
            using var connection = new SpannerConnection(connectionString);
            await connection.CreateDdlCommand(ddl[0].Trim(), extraStatements).ExecuteNonQueryAsync();
        }
    }
}
