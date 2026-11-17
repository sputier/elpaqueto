using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace ElPaqueto.Tests;

/// <summary>Cleanup of the temporary directories of the tests.</summary>
internal static class TestDirectory
{
    private const int Attempts = 5;

    /// <summary>
    /// Deletes <paramref name="path"/> with everything in it. Never throws: a leftover temporary directory must
    /// not fail a test. Pooled SQLite connections are closed first and a locked file is retried briefly, because
    /// Windows refuses to delete a file that is still open.
    /// </summary>
    public static void DeleteQuietly(string path)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                // Pooled connections keep the database files open.
                SqliteConnection.ClearAllPools();

                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt < Attempts)
                {
                    Thread.Sleep(50 * attempt);
                }
            }
        }
    }
}
