﻿//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.SqlParser.Parser;
using Microsoft.SqlTools.ServiceLayer.Connection;
using Microsoft.SqlTools.ServiceLayer.QueryExecution.Contracts;
using Microsoft.SqlTools.ServiceLayer.SqlContext;

namespace Microsoft.SqlTools.ServiceLayer.QueryExecution
{
    /// <summary>
    /// Internal representation of an active query
    /// </summary>
    public class Query : IDisposable
    {
        private const string RowsAffectedFormat = "({0} row(s) affected)";

        #region Properties

        /// <summary>
        /// The batches underneath this query
        /// </summary>
        private Batch[] Batches { get; set; }

        /// <summary>
        /// The summaries of the batches underneath this query
        /// </summary>
        public BatchSummary[] BatchSummaries
        {
            get { return Batches.Select((batch, index) => new BatchSummary
            {
                Id = index,
                HasError = batch.HasError,
                Messages = batch.ResultMessages.ToArray(),
                ResultSetSummaries = batch.ResultSummaries
            }).ToArray(); }
        }

        /// <summary>
        /// Cancellation token source, used for cancelling async db actions
        /// </summary>
        private readonly CancellationTokenSource cancellationSource;

        /// <summary>
        /// The connection info associated with the file editor owner URI, used to create a new
        /// connection upon execution of the query
        /// </summary>
        public ConnectionInfo EditorConnection { get; set; }

        /// <summary>
        /// Whether or not the query has completed executed, regardless of success or failure
        /// </summary>
        public bool HasExecuted
        {
            get { return Batches.All(b => b.HasExecuted); }
        }

        /// <summary>
        /// The text of the query to execute
        /// </summary>
        public string QueryText { get; set; }

        #endregion

        /// <summary>
        /// Constructor for a query
        /// </summary>
        /// <param name="queryText">The text of the query to execute</param>
        /// <param name="connection">The information of the connection to use to execute the query</param>
        /// <param name="settings">Settings for how to execute the query, from the user</param>
        public Query(string queryText, ConnectionInfo connection, QueryExecutionSettings settings)
        {
            // Sanity check for input
            if (String.IsNullOrEmpty(queryText))
            {
                throw new ArgumentNullException(nameof(queryText), "Query text cannot be null");
            }
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection), "Connection cannot be null");
            }

            // Initialize the internal state
            QueryText = queryText;
            EditorConnection = connection;
            cancellationSource = new CancellationTokenSource();

            // Process the query into batches
            ParseResult parseResult = Parser.Parse(queryText, new ParseOptions
            {
                BatchSeparator = settings.BatchSeparator
            });
            Batches = parseResult.Script.Batches.Select(b => new Batch(b.Sql)).ToArray();
        }

        /// <summary>
        /// Executes this query asynchronously and collects all result sets
        /// </summary>
        public async Task Execute()
        {
            // Sanity check to make sure we haven't already run this query
            if (HasExecuted)
            {
                throw new InvalidOperationException("Query has already executed.");
            }

            // Open up a connection for querying the database
            string connectionString = ConnectionService.BuildConnectionString(EditorConnection.ConnectionDetails);
            using (DbConnection conn = EditorConnection.Factory.CreateSqlConnection(connectionString))
            {
                // We need these to execute synchronously, otherwise the user will be very unhappy
                foreach (Batch b in Batches)
                {
                    await b.Execute(conn, cancellationSource.Token);
                }
            }
        }

        /// <summary>
        /// Retrieves a subset of the result sets
        /// </summary>
        /// <param name="batchIndex">The index for selecting the batch item</param>
        /// <param name="resultSetIndex">The index for selecting the result set</param>
        /// <param name="startRow">The starting row of the results</param>
        /// <param name="rowCount">How many rows to retrieve</param>
        /// <returns>A subset of results</returns>
        public ResultSetSubset GetSubset(int batchIndex, int resultSetIndex, int startRow, int rowCount)
        {
            // Sanity check that the results are available
            if (!HasExecuted)
            {
                throw new InvalidOperationException("The query has not completed, yet.");
            }

            // Sanity check to make sure that the batch is within bounds
            if (batchIndex < 0 || batchIndex >= Batches.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(batchIndex), "Result set index cannot be less than 0" +
                                                                             "or greater than the number of result sets");
            }

            return Batches[batchIndex].GetSubset(resultSetIndex, startRow, rowCount);
        }

        /// <summary>
        /// Cancels the query by issuing the cancellation token
        /// </summary>
        public void Cancel()
        {
            // Make sure that the query hasn't completed execution
            if (HasExecuted)
            {
                throw new InvalidOperationException("The query has already completed, it cannot be cancelled.");
            }

            // Issue the cancellation token for the query
            cancellationSource.Cancel();
        }

        #region IDisposable Implementation

        private bool disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
            {
                return;
            }

            if (disposing)
            {
                cancellationSource.Dispose();
            }

            disposed = true;
        }

        ~Query()
        {
            Dispose(false);
        }

        #endregion
    }
}
