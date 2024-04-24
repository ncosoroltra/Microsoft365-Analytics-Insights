﻿using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Common.DataUtils.Sql.Inserts
{
    /// <summary>
    /// For inserting & merging a lot of records quickly. Used when EF is too slow.
    /// Inserts all records into an autogenerated temp table and then merges with a supplied script.
    /// </summary>
    /// <typeparam name="T">Temp table model class type</typeparam>
    public class InsertBatch<T> where T : class
    {
        #region Constructor & Privates

        private readonly string _connectionString;
        private readonly ILogger _telemetry;
        private InsertBatchTypeFieldCache<T> _batchTypeFieldCache = null;
        public InsertBatch(string connectionString, ILogger telemetry)
        {
            _connectionString = connectionString;
            _telemetry = telemetry;
            _batchTypeFieldCache = new InsertBatchTypeFieldCache<T>();
        }

        #endregion

        public List<T> Rows { get; set; } = new List<T>();
        public InsertBatchTypeFieldCache<T> PropCache => _batchTypeFieldCache;

        public async Task<int> SaveToStagingTable(string mergeSql)
        {
            return await SaveToStagingTable(10000, mergeSql);
        }

        public async Task<int> SaveToStagingTable(int insertsPerThread, string mergeSql)
        {
            if (Rows.Count == 0) return 0;

            // Extract/validate schema
            var typeParameterType = typeof(T);

            var tempTableName = string.Empty;
            var tableAtt = typeParameterType.GetCustomAttribute<TempTableNameAttribute>();
            if (tableAtt != null && tableAtt.IsValid)
                tempTableName = tableAtt.Name;
            else
            {
                throw new BatchSaveException($"No valid table-name attribute found on {typeParameterType.Name}");
            }
            if (_batchTypeFieldCache.PropertyMappingInfo.Count == 0)
            {
                throw new BatchSaveException($"No fields found on {typeParameterType.Name}");
            }

            // Do database things
            using (var opGlobalConnection = new SqlConnection(_connectionString))
            {
                await opGlobalConnection.OpenAsync();

                // Prepare table
                await DropAndCreateStagingTable(opGlobalConnection, tempTableName, _batchTypeFieldCache.PropertyMappingInfo.Select(d => d.SqlInfo).ToList());

                // Import data into staging table
                var loader = new ParallelListProcessor<T>(insertsPerThread);

                // Import in parallel
                await loader.ProcessListInParallel(Rows,
                    async (threadListChunk, threadIndex) => await ProcessChunkAsync(tempTableName, _telemetry, threadListChunk, threadIndex),
                    threads => _telemetry.LogInformation($"Inserting {Rows.Count.ToString("n0")} records into {tempTableName}, across {threads} thread(s)..."));

                // Merge with supplied SQL
                if (!string.IsNullOrEmpty(mergeSql))
                {
                    var cmd = opGlobalConnection.CreateCommand();
                    cmd.CommandText = mergeSql;
                    cmd.CommandTimeout = 0;
                    try
                    {
                        return await cmd.ExecuteNonQueryAsync();
                    }
                    catch (SqlException ex)
                    {
                        throw new BatchSaveException($"Couldn't merge batch insert using given SQL: {ex.Message}");
                    }
                }
                else
                {
                    return 0;
                }
            }
        }

        private async Task ProcessChunkAsync(string tempTableName, ILogger telemetry, List<T> threadListChunk, int chunkIdx)
        {
            using (var chunkSqlConnection = new SqlConnection(_connectionString))
            {
                await chunkSqlConnection.OpenAsync();

                // Build SQL statement
                string fieldNamesSnippet = string.Empty, fieldVarsSnippet = string.Empty;
                int fieldDefIdx = 0;
                foreach (var fieldMapping in _batchTypeFieldCache.PropertyMappingInfo)
                {
                    fieldNamesSnippet += $"[{fieldMapping.SqlInfo.FieldName}], ";
                    fieldVarsSnippet += $"@p{fieldDefIdx}, ";
                    fieldDefIdx++;
                }
                fieldNamesSnippet = fieldNamesSnippet.TrimEnd(", ".ToCharArray());
                fieldVarsSnippet = fieldVarsSnippet.TrimEnd(", ".ToCharArray());

                var cmd = chunkSqlConnection.CreateCommand();
                var sqlInsert = Environment.NewLine + $"insert into [{tempTableName}] ({fieldNamesSnippet}) values ({fieldVarsSnippet})";
                cmd.CommandText = sqlInsert;

                var typeParameterType = typeof(T);

                // Add data
                foreach (var insertObj in threadListChunk)
                {
                    cmd.Parameters.Clear();
                    int fieldIdx = 0;
                    foreach (var fieldMapping in _batchTypeFieldCache.PropertyMappingInfo)
                    {
                        var objVal = fieldMapping.Property.GetValue(insertObj);
                        if (objVal == null && !fieldMapping.SqlInfo.Nullable)
                        {
                            throw new BatchSaveException($"Couldn't insert null into batch insert field '{fieldMapping.SqlInfo.FieldName}' in table '{tempTableName}'");
                        }
                        cmd.ParamUp("@p" + fieldIdx, objVal, fieldMapping.SqlInfo.SqlType);

                        fieldIdx++;
                    }
                    try
                    {
                        await cmd.ExecuteNonQueryAsync();
                    }
                    catch (SqlException ex)
                    {
                        _telemetry.LogCritical($"Failed to insert record into {tempTableName} - {sqlInsert}: {ex.Message}");
                        throw;
                    }
                }
#if DEBUG
                Console.Write($"Done:#{chunkIdx}...");
#endif
            }
        }

        private async Task DropAndCreateStagingTable(SqlConnection opConnection, string tableName, List<ColumnSqlInfo> fields)
        {
            // Create staging table
            var createTableCmd = opConnection.CreateCommand();

            // Build SQL statement
            var fieldDefsSnippet = string.Empty;
            foreach (var field in fields)
            {
                var collateStr = string.Empty;
                if (!string.IsNullOrEmpty(field.ColationOverride))
                {
                    collateStr = $"COLLATE {field.ColationOverride}";
                }
                var nullableStr = field.Nullable ? "NULL" : "NOT NULL";
                fieldDefsSnippet += $"[{field.FieldName}] {field.SqlColDefinition} {collateStr} {nullableStr}, ";
            }
            fieldDefsSnippet = fieldDefsSnippet.TrimEnd(", ".ToCharArray());

            // Normal table or temp table¿
            var tempTableNameFull = tableName;
            if (tableName.StartsWith("#"))
            {
                tempTableNameFull = $"tempdb..{tableName}";
            }
            var sql =
                $"IF OBJECT_ID (N'{tempTableNameFull}', N'U') IS NOT NULL drop table[{tableName}] " + Environment.NewLine + Environment.NewLine +

                $"CREATE TABLE [dbo].[{tableName}]" +
                $"([id][int] IDENTITY(1, 1) NOT NULL primary key, {fieldDefsSnippet});";


            createTableCmd.CommandText = sql;

            await createTableCmd.ExecuteNonQueryAsync();
        }

    }
}
