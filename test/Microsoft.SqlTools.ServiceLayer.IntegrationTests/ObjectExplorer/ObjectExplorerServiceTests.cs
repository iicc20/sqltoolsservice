﻿//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlTools.ServiceLayer.Connection.Contracts;
using Microsoft.SqlTools.ServiceLayer.ObjectExplorer;
using Microsoft.SqlTools.ServiceLayer.ObjectExplorer.Contracts;
using Microsoft.SqlTools.ServiceLayer.ObjectExplorer.Nodes;
using Microsoft.SqlTools.ServiceLayer.SqlContext;
using Microsoft.SqlTools.ServiceLayer.Test.Common;
using Microsoft.SqlTools.ServiceLayer.Test.Common.Extensions;
using Microsoft.SqlTools.ServiceLayer.Workspace;
using NUnit.Framework;
using static Microsoft.SqlTools.ServiceLayer.ObjectExplorer.ObjectExplorerService;

namespace Microsoft.SqlTools.ServiceLayer.IntegrationTests.ObjectExplorer
{
    public partial class ObjectExplorerServiceTests
    {
        private ObjectExplorerService _service = TestServiceProvider.Instance.ObjectExplorerService;

        [Test]
        public async Task CreateSessionAndExpandOnTheServerShouldReturnServerAsTheRoot()
        {
            var query = "";
            string databaseName = null;
            await RunTest(databaseName, query, "EmptyDatabase", async (testDbName, session) =>
            {
                await ExpandServerNodeAndVerifyDatabaseHierachy(testDbName, session);
            });
        }

        [Test]
        public async Task CreateSessionWithTempdbAndExpandOnTheServerShouldReturnServerAsTheRoot()
        {
            var query = "";
            string databaseName = "tempdb";
            await RunTest(databaseName, query, "TepmDb", async (testDbName, session) =>
            {
                await ExpandServerNodeAndVerifyDatabaseHierachy(testDbName, session);
            });
        }

        [Test]
        public async Task VerifyServerLogins()
        {
            var query = $@"If Exists (select loginname from master.dbo.syslogins
                            where name = 'OEServerLogin')
                        Begin
                            Drop Login  [OEServerLogin]
                        End

                        CREATE LOGIN OEServerLogin WITH PASSWORD = '{Guid.NewGuid()}'
                        GO
                        ALTER LOGIN OEServerLogin DISABLE; ";
            string databaseName = "tempdb";
            await RunTest(databaseName, query, "TepmDb", async (testDbName, session) =>
            {
                var serverChildren = (await _service.ExpandNode(session, session.Root.GetNodePath())).Nodes;
                var securityNode = serverChildren.FirstOrDefault(x => x.Label == SR.SchemaHierarchy_Security);
                var securityChildren = (await _service.ExpandNode(session, securityNode.NodePath)).Nodes;
                var loginsNode = securityChildren.FirstOrDefault(x => x.Label == SR.SchemaHierarchy_Logins);
                var loginsChildren = (await _service.ExpandNode(session, loginsNode.NodePath)).Nodes;
                var login = loginsChildren.FirstOrDefault(x => x.Label == "OEServerLogin");
                Assert.NotNull(login);

                Assert.True(login.NodeStatus == "Disabled");
                await TestServiceProvider.Instance.RunQueryAsync(TestServerType.OnPrem, testDbName, "Drop Login  OEServerLogin");

            });
        }

        [Test]
        public async Task VerifyServerTriggers()
        {
            var query = @"IF EXISTS (SELECT * FROM sys.server_triggers  WHERE name = 'OE_ddl_trig_database')

                        Begin
                            DROP TRIGGER OE_ddl_trig_database  ON ALL SERVER

                        ENd
                        GO

                        CREATE TRIGGER OE_ddl_trig_database
                        ON ALL SERVER
                        FOR CREATE_DATABASE
                        AS
                            PRINT 'Database Created.'
                        GO
                        GO
                        Disable TRIGGER OE_ddl_trig_database ON ALL SERVER ;";
            string databaseName = "tempdb";
            await RunTest(databaseName, query, "TepmDb", async (testDbName, session) =>
            {
                var serverChildren = (await _service.ExpandNode(session, session.Root.GetNodePath())).Nodes;
                var serverObjectsNode = serverChildren.FirstOrDefault(x => x.Label == SR.SchemaHierarchy_ServerObjects);
                var serverObjectsChildren = (await _service.ExpandNode(session, serverObjectsNode.NodePath)).Nodes;
                var triggersNode = serverObjectsChildren.FirstOrDefault(x => x.Label == SR.SchemaHierarchy_Triggers);
                var triggersChildren = await _service.ExpandNode(session, triggersNode.NodePath);
                var trigger = triggersChildren.Nodes.FirstOrDefault(x => x.Label == "OE_ddl_trig_database");
                Assert.NotNull(trigger);

                Assert.True(trigger.NodeStatus == "Disabled");
                await TestServiceProvider.Instance.RunQueryAsync(TestServerType.OnPrem, testDbName, "DROP TRIGGER OE_ddl_trig_database");

            });
        }

        [Test]
        public async Task CreateSessionAndExpandOnTheDatabaseShouldReturnDatabaseAsTheRoot()
        {
            var query = "";
            string databaseName = "#testDb#";
            await RunTest(databaseName, query, "TestDb", ExpandAndVerifyDatabaseNode);
        }

        [Test]
        public async Task RefreshNodeShouldGetTheDataFromDatabase()
        {
            var query = "Create table t1 (c1 int)";
            string databaseName = "#testDb#";
            await RunTest(databaseName, query, "TestDb", async (testDbName, session) =>
            {
                var tablesNode = await FindNodeByLabel(session.Root.ToNodeInfo(), session, SR.SchemaHierarchy_Tables);
                var tableChildren = (await _service.ExpandNode(session, tablesNode.NodePath)).Nodes;
                string dropTableScript = "Drop Table t1";
                Assert.True(tableChildren.Any(t => t.Label == "dbo.t1"));
                await TestServiceProvider.Instance.RunQueryAsync(TestServerType.OnPrem, testDbName, dropTableScript);
                tableChildren = (await _service.ExpandNode(session, tablesNode.NodePath)).Nodes;
                Assert.True(tableChildren.Any(t => t.Label == "dbo.t1"));
                tableChildren = (await _service.ExpandNode(session, tablesNode.NodePath, true)).Nodes;
                Assert.False(tableChildren.Any(t => t.Label == "dbo.t1"));

            });
        }

        /// <summary>
        /// Create a test database with prefix (OfflineDb). Create an oe session for master db and expand the new test db.
        /// The expand should return an error that says database if offline
        /// </summary>
        [Test]
        public async Task ExpandOfflineDatabaseShouldReturnError()
        {
            var query = "ALTER DATABASE {0} SET OFFLINE WITH ROLLBACK IMMEDIATE";
            string databaseName = "master";

            await RunTest(databaseName, query, "OfflineDb", async (testDbName, session) =>
            {
                var databaseNode = await ExpandServerNodeAndVerifyDatabaseHierachy(testDbName, session);
                var response = await _service.ExpandNode(session, databaseNode.NodePath);
                Assert.True(response.ErrorMessage.Contains(string.Format(CultureInfo.InvariantCulture, SR.DatabaseNotAccessible, testDbName)));
            });
        }

        [Test]
        public async Task RefreshShouldCleanTheCache()
        {
            string query = @"Create table t1 (c1 int)
                            GO
                            Create table t2 (c1 int)
                            GO";
            string dropTableScript1 = "Drop Table t1";
            string createTableScript2 = "Create table t3 (c1 int)";

            string databaseName = "#testDb#";
            await RunTest(databaseName, query, "TestDb", async (testDbName, session) =>
            {
                var tablesNode = await FindNodeByLabel(session.Root.ToNodeInfo(), session, SR.SchemaHierarchy_Tables);

                //Expand Tables node
                var tableChildren = await _service.ExpandNode(session, tablesNode.NodePath);

                //Expanding the tables return t1
                Assert.True(tableChildren.Nodes.Any(t => t.Label == "dbo.t1"));

                //Delete the table from db
                await TestServiceProvider.Instance.RunQueryAsync(TestServerType.OnPrem, testDbName, dropTableScript1);

                //Expand Tables node
                tableChildren = await _service.ExpandNode(session, tablesNode.NodePath);

                //Tables still includes t1
                Assert.True(tableChildren.Nodes.Any(t => t.Label == "dbo.t1"));

                //Verify the tables cache has items

                var rootChildrenCache = session.Root.GetChildren();
                var tablesCache = rootChildrenCache.First(x => x.Label == SR.SchemaHierarchy_Tables).GetChildren();
                Assert.True(tablesCache.Any());

                await VerifyRefresh(session, tablesNode.NodePath, "dbo.t1");
                //Delete the table from db
                await TestServiceProvider.Instance.RunQueryAsync(TestServerType.OnPrem, testDbName, createTableScript2);
                await VerifyRefresh(session, tablesNode.NodePath, "dbo.t3", false);

            });
        }

        [Test]
        public async Task GroupBySchemaisDisabled()
        {
            string query = @"Create schema t1
                            GO
                            Create schema t2
                            GO";
            string databaseName = "#testDb#";
            await RunTest(databaseName, query, "TestDb", async (testDbName, session) =>
            {
                WorkspaceService<SqlToolsSettings>.Instance.CurrentSettings.SqlTools.ObjectExplorer = new ObjectExplorerSettings() { GroupBySchema = false };
                var databaseNode = session.Root.ToNodeInfo();
                var databaseChildren = await _service.ExpandNode(session, databaseNode.NodePath);
                Assert.True(databaseChildren.Nodes.Any(t => t.Label == SR.SchemaHierarchy_Tables), "Tables node should be found in database node when group by schema is disabled");
                Assert.True(databaseChildren.Nodes.Any(t => t.Label == SR.SchemaHierarchy_Views), "Views node should be found in database node when group by schema is disabled");
            });
        }

        [Test]
        public async Task GroupBySchemaisEnabled()
        {
            string query = @"Create schema t1
                            GO
                            Create schema t2
                            GO";
            string databaseName = "#testDb#";
            await RunTest(databaseName, query, "TestDb", async (testDbName, session) =>
            {
                WorkspaceService<SqlToolsSettings>.Instance.CurrentSettings.SqlTools.ObjectExplorer = new ObjectExplorerSettings() { GroupBySchema = true };
                var databaseNode = session.Root.ToNodeInfo();
                var databaseChildren = await _service.ExpandNode(session, databaseNode.NodePath);
                Assert.True(databaseChildren.Nodes.Any(t => t.Label == "t1"), "Schema node t1 should be found in database node when group by schema is enabled");
                Assert.True(databaseChildren.Nodes.Any(t => t.Label == "t2"), "Schema node t2 should be found in database node when group by schema is enabled");
                Assert.False(databaseChildren.Nodes.Any(t => t.Label == SR.SchemaHierarchy_Tables), "Tables node should not be found in database node when group by schema is enabled");
                Assert.False(databaseChildren.Nodes.Any(t => t.Label == SR.SchemaHierarchy_Views), "Views node should not be found in database node when group by schema is enabled");
                Assert.True(databaseChildren.Nodes.Any(t => t.Label == SR.SchemaHierarchy_Programmability), "Programmability node should be found in database node when group by schema is enabled");
                var lastSchemaPosition = Array.FindLastIndex(databaseChildren.Nodes, t => t.ObjectType == nameof(NodeTypes.ExpandableSchema));
                var firstNonSchemaPosition = Array.FindIndex(databaseChildren.Nodes, t => t.ObjectType != nameof(NodeTypes.ExpandableSchema));
                Assert.True(lastSchemaPosition < firstNonSchemaPosition, "Schema nodes should be before non-schema nodes");
                WorkspaceService<SqlToolsSettings>.Instance.CurrentSettings.SqlTools.ObjectExplorer = new ObjectExplorerSettings() { GroupBySchema = false };
            });
        }

        [Test]
        public async Task GroupBySchemaHidesLegacySchemas()
        {
            string query = @"Create schema t1
                            GO
                            Create schema t2
                            GO";
            string databaseName = "#testDb#";
            await RunTest(databaseName, query, "TestDb", async (testDbName, session) =>
            {
                WorkspaceService<SqlToolsSettings>.Instance.CurrentSettings.SqlTools.ObjectExplorer = new ObjectExplorerSettings() { GroupBySchema = true };
                var databaseNode = session.Root.ToNodeInfo();
                var databaseChildren = await _service.ExpandNode(session, databaseNode.NodePath);
                Assert.True(databaseChildren.Nodes.Any(t => t.Label == "t1"), "Non legacy schema node t1 should be found in database node when group by schema is enabled");
                Assert.True(databaseChildren.Nodes.Any(t => t.Label == "t2"), "Non legacy schema node t2 should be found in database node when group by schema is enabled");
                string[] legacySchemas = new string[]
                {
                    "db_accessadmin",
                    "db_backupoperator",
                    "db_datareader",
                    "db_datawriter",
                    "db_ddladmin",
                    "db_denydatareader",
                    "db_denydatawriter",
                    "db_owner",
                    "db_securityadmin"
                };
                foreach (var nodes in databaseChildren.Nodes)
                {
                    Assert.That(legacySchemas, Does.Not.Contain(nodes.Label), "Legacy schema node should not be found in database node when group by schema is enabled");
                }
                var legacySchemasNode = databaseChildren.Nodes.First(t => t.Label == SR.SchemaHierarchy_BuiltInSchema);
                var legacySchemasChildren = await _service.ExpandNode(session, legacySchemasNode.NodePath);
                foreach (var nodes in legacySchemasChildren.Nodes)
                {
                    Assert.That(legacySchemas, Does.Contain(nodes.Label), "Legacy schema nodes should be found in legacy schemas folder when group by schema is enabled");
                }
                WorkspaceService<SqlToolsSettings>.Instance.CurrentSettings.SqlTools.ObjectExplorer = new ObjectExplorerSettings() { GroupBySchema = false };
            });
        }

        [Test]
        public async Task VerifyOEFilters()
        {
            string query = @"
            Create table ""table['^__']"" (id int);
            Create table ""table['^__']2"" (id int);
            Create table ""table['^%%2"" (id int);
            Create table ""testTable"" (id int);
            ";

            string databaseName = "#testDb#";
            await RunTest(databaseName, query, "Testdb", async (testDbName, session) =>
            {
                var databaseNode = session.Root.ToNodeInfo();
                var databaseChildren = await _service.ExpandNode(session, databaseNode.NodePath);
                Assert.True(databaseChildren.Nodes.Any(t => t.Label == SR.SchemaHierarchy_Tables), "Tables node should be found in database node");
                var tablesNode = databaseChildren.Nodes.First(t => t.Label == SR.SchemaHierarchy_Tables);
                var NameProperty = tablesNode.FilterableProperties.First(t => t.Name == "Name");
                Assert.True(NameProperty != null, "Name property should be found in tables node");

                // Testing contains operator
                NodeFilter filter = new NodeFilter()
                {
                    Name = NameProperty.Name,
                    Value = "table",
                    Operator = NodeFilterOperator.Contains
                };
                var tablesChildren = await _service.ExpandNode(session, tablesNode.NodePath, true, null, new NodeFilter[] { filter });
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.testTable"), "testTable node should be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^__']"), "table['^__'] node should be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^__']2"), "table['^__']2 node should be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^%%2"), "table['^%%2 node should be found in tables node");

                // Testing starts with operator
                filter = new NodeFilter()
                {
                    Name = NameProperty.Name,
                    Value = "table['^__']",
                    Operator = NodeFilterOperator.StartsWith
                };
                tablesChildren = await _service.ExpandNode(session, tablesNode.NodePath, true, null, new NodeFilter[] { filter });
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^__']"), "table['^__'] node should be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^__']2"), "table['^__']2 node should be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.testTable") == false, "testTable node should not be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^%%2") == false, "table['^%%2 node should not be found in tables node");

                // Testing starts with operator
                filter = new NodeFilter()
                {
                    Name = NameProperty.Name,
                    Value = "table['^%%2",
                    Operator = NodeFilterOperator.StartsWith
                };
                tablesChildren = await _service.ExpandNode(session, tablesNode.NodePath, true, null, new NodeFilter[] { filter });
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^%%2"), "table['^%%2 node should be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^__']") == false, "table['^__'] node should not be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^__']2") == false, "table['^__']2 node should not be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.testTable") == false, "testTable node should not be found in tables node");


                // Testing ends with operator
                filter = new NodeFilter()
                {
                    Name = NameProperty.Name,
                    Value = "table['^__']",
                    Operator = NodeFilterOperator.EndsWith
                };
                tablesChildren = await _service.ExpandNode(session, tablesNode.NodePath, true, null, new NodeFilter[] { filter });
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^__']"), "table['^__'] node should be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^__']2") == false, "table['^__']2 node should not be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.testTable") == false, "testTable node should not be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^%%2") == false, "table['^%%2 node should not be found in tables node");

                // Testing equals operator
                filter = new NodeFilter()
                {
                    Name = NameProperty.Name,
                    Value = "table['^__']",
                    Operator = NodeFilterOperator.Equals
                };
                tablesChildren = await _service.ExpandNode(session, tablesNode.NodePath, true, null, new NodeFilter[] { filter });
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^__']"), "table['^__'] node should be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^__']2") == false, "table['^__']2 node should not be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.testTable") == false, "testTable node should not be found in tables node");
                Assert.That(tablesChildren.Nodes.Any(t => t.Label == "dbo.table['^%%2") == false, "table['^%%2 node should not be found in tables node");
                
            });
        }

        private async Task VerifyRefresh(ObjectExplorerSession session, string tablePath, string tableName, bool deleted = true)
        {
            //Refresh Root
            var rootChildren = await _service.ExpandNode(session, session.Root.ToNodeInfo().NodePath, true);

            //Verify tables cache is empty
            var rootChildrenCache = session.Root.GetChildren();
            var tablesCache = rootChildrenCache.First(x => x.Label == SR.SchemaHierarchy_Tables).GetChildren();
            Assert.False(tablesCache.Any());

            //Expand Tables
            var tableChildren = (await _service.ExpandNode(session, tablePath, true)).Nodes;

            //Verify table is not returned
            Assert.AreEqual(tableChildren.Any(t => t.Label == tableName), !deleted);

            //Verify tables cache has items
            rootChildrenCache = session.Root.GetChildren();
            tablesCache = rootChildrenCache.First(x => x.Label == SR.SchemaHierarchy_Tables).GetChildren();
            Assert.True(tablesCache.Any());
        }

        [Test]
        public async Task VerifyAllSqlObjects()
        {
            var queryFileName = "AllSqlObjects.sql";
            string baselineFileName = "AllSqlObjects.txt";
            string databaseName = "#testDb#";
            await TestServiceProvider.CalculateRunTime(() => VerifyObjectExplorerTest(databaseName, "AllSqlObjects", queryFileName, baselineFileName), true);
        }

        //[Test]
        //This takes take long to run so not a good test for CI builds
        public async Task VerifySystemObjects()
        {
            string queryFileName = string.Empty;
            string baselineFileName = string.Empty;
            string databaseName = "#testDb#";
            await TestServiceProvider.CalculateRunTime(() => VerifyObjectExplorerTest(databaseName, queryFileName, "SystemOBjects", baselineFileName, true), true);
        }

        private async Task RunTest(string databaseName, string query, string testDbPrefix, Func<string, ObjectExplorerSession, Task> test)
        {
            SqlTestDb testDb = null;
            string uri = string.Empty;
            try
            {
                testDb = await SqlTestDb.CreateNewAsync(TestServerType.OnPrem, false, null, query, testDbPrefix);
                if (databaseName == "#testDb#")
                {
                    databaseName = testDb.DatabaseName;
                }

                var session = await CreateSession(databaseName);
                uri = session.Uri;
                await test(testDb.DatabaseName, session);
            }
            catch (Exception ex)
            {
                string msg = ex.BuildRecursiveErrorMessage();
                throw new Exception($"Failed to run OE test. uri:{uri} error:{msg} {ex.StackTrace}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(uri))
                {
                    CloseSession(uri);
                }
                if (testDb != null)
                {
                    await testDb.CleanupAsync();
                }
            }
        }

        private async Task<ObjectExplorerSession> CreateSession(string databaseName)
        {
            ConnectParams connectParams = TestServiceProvider.Instance.ConnectionProfileService.GetConnectionParameters(TestServerType.OnPrem, databaseName);
            //connectParams.Connection.Pooling = false;
            ConnectionDetails details = connectParams.Connection;
            string uri = GenerateUri(details);

            var session = await _service.DoCreateSession(details, uri);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "OE session created for database: {0}", databaseName));
            return session;
        }

        private async Task<NodeInfo> ExpandServerNodeAndVerifyDatabaseHierachy(string databaseName, ObjectExplorerSession session, bool serverNode = true)
        {
            Assert.That(session, Is.Not.Null, nameof(session));
            Assert.That(session.Root, Is.Not.Null, nameof(session.Root));
            var nodeInfo = session.Root.ToNodeInfo();
            Assert.That(nodeInfo.IsLeaf, Is.False, "Should not be a leaf node");

            NodeInfo databaseNode = null;

            if (serverNode)
            {
                Assert.That(nodeInfo.NodeType, Is.EqualTo(NodeTypes.Server.ToString()), "Server node has incorrect type");
                var children = session.Root.Expand(new CancellationToken());

                //All server children should be folder nodes
                foreach (var item in children)
                {
                    Assert.That(item.NodeType, Is.EqualTo(NodeTypes.Folder.ToString()), $"Node {item.Label} should be folder");
                }

                var databasesRoot = children.FirstOrDefault(x => x.NodeTypeId == NodeTypes.Databases);
                var databasesChildren = (await _service.ExpandNode(session, databasesRoot.GetNodePath())).Nodes;
                var databases = databasesChildren.Where(x => x.NodeType == NodeTypes.Database.ToString());

                // Verify the test databases is in the list
                Assert.False(databases.Any(x => x.Label == "master"));
                Assert.That(databases, Has.None.Matches<NodeInfo>(n => n.Label == "master"), "master database not expected in user databases folder");
                var systemDatabasesNodes = databasesChildren.Where(x => x.Label == SR.SchemaHierarchy_SystemDatabases).ToList();
                Assert.That(systemDatabasesNodes, Has.Count.EqualTo(1), $"Exactly one {SR.SchemaHierarchy_SystemDatabases} node expected");

                var expandResponse = await _service.ExpandNode(session, systemDatabasesNodes.First().NodePath);
                Assert.That(expandResponse.Nodes, Has.One.Matches<NodeInfo>(n => n.Label == "master"), "master database expected in system databases folder");

                databaseNode = databases.FirstOrDefault(d => d.Label == databaseName);
            }
            else
            {
                Assert.That(nodeInfo.NodeType, Is.EqualTo(NodeTypes.Database.ToString()), $"Database node {nodeInfo.Label} has incorrect type");
                databaseNode = session.Root.ToNodeInfo();
                Assert.True(databaseNode.Label.Contains(databaseName));
                var databasesChildren = (await _service.ExpandNode(session, databaseNode.NodePath)).Nodes;
                Assert.False(databasesChildren.Any(x => x.Label == SR.SchemaHierarchy_SystemDatabases));

            }
            Assert.That(databaseNode, Is.Not.Null, "Database node should not be null");
            return databaseNode!;
        }

        private async Task ExpandAndVerifyDatabaseNode(string databaseName, ObjectExplorerSession session)
        {
            Assert.NotNull(session);
            Assert.NotNull(session.Root);
            var nodeInfo = session.Root.ToNodeInfo();
            Assert.AreEqual(false, nodeInfo.IsLeaf);
            Assert.AreEqual(nodeInfo.NodeType, NodeTypes.Database.ToString());
            Assert.True(nodeInfo.Label.Contains(databaseName));
            var children = (await _service.ExpandNode(session, session.Root.GetNodePath())).Nodes;

            //All server children should be folder nodes
            foreach (var item in children)
            {
                Assert.AreEqual("Folder", item.NodeType);
            }

            var tablesRoot = children.FirstOrDefault(x => x.Label == SR.SchemaHierarchy_Tables);
            Assert.NotNull(tablesRoot);
        }

        private void CloseSession(string uri)
        {
            _service.CloseSession(uri);
            Console.WriteLine($"Session closed uri:{uri}");
        }

        private async Task ExpandTree(NodeInfo node, ObjectExplorerSession session, StringBuilder stringBuilder = null, bool verifySystemObjects = false)
        {
            if (node != null && !node.IsLeaf)
            {
                var children = (await _service.ExpandNode(session, node.NodePath)).Nodes;
                foreach (var child in children)
                {
                    VerifyMetadata(child);
                    if (stringBuilder != null && child.NodeType != "Folder" && child.NodeType != "FileGroupFile")
                    {
                        stringBuilder.Append($"NodeType: {child.NodeType} Label: {child.Label} SubType:{child.NodeSubType} Status:{child.NodeStatus}{Environment.NewLine}");
                    }
                    if (!verifySystemObjects && (child.Label == SR.SchemaHierarchy_SystemStoredProcedures ||
                        child.Label == SR.SchemaHierarchy_SystemViews ||
                        child.Label == SR.SchemaHierarchy_SystemFunctions ||
                        child.Label == SR.SchemaHierarchy_SystemDataTypes))
                    {
                        // don't expand the system folders because then the test will take for ever
                    }
                    else
                    {
                        await ExpandTree(child, session, stringBuilder, verifySystemObjects);
                    }
                }
            }
        }

        /// <summary>
        /// Returns the node with the given label
        /// </summary>
        private async Task<NodeInfo> FindNodeByLabel(NodeInfo node, ObjectExplorerSession session, string label)
        {
            if (node != null && node.Label == label)
            {
                return node;
            }
            else if (node != null && !node.IsLeaf)
            {
                var response = await _service.ExpandNode(session, node.NodePath);
                var children = response.Nodes;
                Assert.NotNull(children);
                foreach (var child in children)
                {
                    VerifyMetadata(child);
                    if (child.Label == label)
                    {
                        return child;
                    }
                    var result = await FindNodeByLabel(child, session, label);
                    if (result != null)
                    {
                        return result;
                    }
                }
            }

            return null;
        }

        private void VerifyMetadata(NodeInfo node)
        {
            // These are node types for which the label doesn't include a schema
            // (usually because the objects themselves aren't schema-bound)
            var schemalessLabelNodeTypes = new List<string>() {
                "Column",
                "Key",
                "Constraint",
                "Index",
                "Statistic",
                "Trigger",
                "StoredProcedureParameter",
                "TableValuedFunctionParameter",
                "ScalarValuedFunctionParameter",
                "UserDefinedTableTypeColumn"
            };
            if (node.NodeType != "Folder")
            {
                Assert.That(node.NodeType, Is.Not.Empty.Or.Null, "NodeType should not be empty or null");
                if (node.Metadata != null && !string.IsNullOrEmpty(node.Metadata.MetadataTypeName))
                {
                    if (!string.IsNullOrEmpty(node.Metadata.Schema) && !schemalessLabelNodeTypes.Any(t => t == node.NodeType))
                    {
                        Assert.That(node.Label, Does.Contain($"{node.Metadata.Schema}.{node.Metadata.Name}"), "Node label does not contain expected text");
                    }
                    else
                    {
                        Assert.That(node.Label, Does.Contain(node.Metadata.Name), "Node label does not contain expected text");
                    }
                }
            }
        }

        private async Task<bool> VerifyObjectExplorerTest(string databaseName, string testDbPrefix, string queryFileName, string baselineFileName, bool verifySystemObjects = false)
        {
            var query = string.IsNullOrEmpty(queryFileName) ? string.Empty : LoadScript(queryFileName);
            var stringBuilder = new StringBuilder();
            await RunTest(databaseName, query, testDbPrefix, async (testDbName, session) =>
            {
                await ExpandServerNodeAndVerifyDatabaseHierachy(testDbName, session, false);
                await ExpandTree(session.Root.ToNodeInfo(), session, stringBuilder, verifySystemObjects);
                string baseline = string.IsNullOrEmpty(baselineFileName) ? string.Empty : LoadBaseLine(baselineFileName);
                if (!string.IsNullOrEmpty(baseline))
                {
                    string actual = stringBuilder.ToString();

                    // Dropped ledger objects have a randomly generated GUID appended to their name when they are deleted
                    // For testing purposes, those guids need to be replaced with a deterministic string
                    actual = GetBaselineRegex().Replace(actual, "<<NonDeterministic>>");

                    // Write output to a bin directory for easier comparison
                    string assemblyPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                    string outputRegeneratedFolder = Path.Combine(assemblyPath, @"ObjectExplorerServiceTests\Baselines\Regenerated");
                    string outputRegeneratedFilePath = Path.Combine(outputRegeneratedFolder, baselineFileName);
                    string msg = "";

                    try
                    {
                        Directory.CreateDirectory(outputRegeneratedFolder);
                        File.WriteAllText(outputRegeneratedFilePath, actual);
                        msg = $"Generated output written to :\t{outputRegeneratedFilePath}\n\t" +
                              $"Baseline output located at  :\t{GetBaseLineFile(baselineFileName)}";
                    }
                    catch (Exception e)
                    {
                        // We don't want to fail the test completely if we failed to write the regenerated baseline
                        // (especially if the test passed).
                        msg = $"Errors also occurred while attempting to write the new baseline file {outputRegeneratedFilePath} : {e.Message}";
                    }

                    Assert.That(actual, Is.EqualTo(baseline), $"Baseline comparison for {baselineFileName} failed\n\t" + msg);
                }
            });

            return true;
        }

        private static string TestLocationDirectory
        {
            get
            {
                return Path.Combine(RunEnvironmentInfo.GetTestDataLocation(), "ObjectExplorer");
            }
        }

        public DirectoryInfo InputFileDirectory
        {
            get
            {
                string d = Path.Combine(TestLocationDirectory, "TestScripts");
                return new DirectoryInfo(d);
            }
        }

        public DirectoryInfo BaselineFileDirectory
        {
            get
            {
                string d = Path.Combine(TestLocationDirectory, "Baselines");
                return new DirectoryInfo(d);
            }
        }

        public FileInfo GetInputFile(string fileName)
        {
            return new FileInfo(Path.Combine(InputFileDirectory.FullName, fileName));
        }

        public FileInfo GetBaseLineFile(string fileName)
        {
            return new FileInfo(Path.Combine(BaselineFileDirectory.FullName, fileName));
        }

        private string LoadScript(string fileName)
        {
            FileInfo inputFile = GetInputFile(fileName);
            return TestUtilities.ReadTextAndNormalizeLineEndings(inputFile.FullName);
        }

        private string LoadBaseLine(string fileName)
        {
            FileInfo inputFile = GetBaseLineFile(fileName);
            return TestUtilities.ReadTextAndNormalizeLineEndings(inputFile.FullName);
        }

        [GeneratedRegex("[A-Z0-9]{32}")]
        private static partial Regex GetBaselineRegex();
    }
}
