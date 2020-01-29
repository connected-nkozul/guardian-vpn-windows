﻿// <copyright file="Update.cs" company="Mozilla">
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed with this file, you can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright(C) 2019 Edge Security LLC. All Rights Reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace FirefoxPrivateNetwork.Update
{
    /// <summary>
    /// Application update functionality.
    /// </summary>
    internal class Update
    {
        private const int MaxMsiDownloadSize = 100 * 1024 * 1024; // 100MiB
        private const int MaxHttpRetries = 3;
        private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(20);

        private static readonly Dictionary<string, HashAlgorithmName> FileHashAlgorithms = new Dictionary<string, HashAlgorithmName>()
        {
            { "sha512", HashAlgorithmName.SHA512 },
        };

        private static readonly UpdateHttpClient UpdateHttpClient = new UpdateHttpClient(MaxMsiDownloadSize);

        /// <summary>
        /// Result values for the Run() method.
        /// </summary>
        public enum UpdateResult
        {
            /// <summary>
            /// Request has successfully been executed, but there are no new updates.
            /// </summary>
            SuccessNoUpdate = 0,

            /// <summary>
            /// Request has successfully been executed, new updates are available and have been executed.
            /// </summary>
            Success,

            /// <summary>
            /// New updates are available, but the downloaded file's signature is invalid.
            /// </summary>
            InvalidSignature,

            /// <summary>
            /// New updates are available, but we were unable to disconnect before attempting to execute the update process.
            /// </summary>
            DisconnectTimeout,

            /// <summary>
            /// Could not retrieve update information or files due to an HTTP error.
            /// </summary>
            HttpError,

            /// <summary>
            /// New updates are available, signatures are valid, but there was an error when attempting to run the MSI update file.
            /// </summary>
            RunFailure,

            /// <summary>
            /// New updates are available, but we cannot access the MSI update file due to georestrictions.
            /// </summary>
            Georestricted,

            /// <summary>
            /// Unspecified failure.
            /// </summary>
            GeneralFailure,
        }

        private enum UpdateDownloadResult
        {
            Success = 0,
            GeneralFailure,
            Georestricted,
        }

        /// <summary>
        /// Starts the update process, downloads the MSI file from Balrog and launches it.
        /// </summary>
        /// <param name="currentVersion">current version of the application.</param>
        /// <returns>Returns an awaitable boolean value indicating whether the update has succeeded or not.</returns>
        public static async Task<UpdateResult> Run(string currentVersion)
        {
            var balrogResponse = default(JSONStructures.BalrogResponse);
            try
            {
                balrogResponse = await Balrog.QueryUpdate(currentVersion);
            }
            catch (HttpRequestException e)
            {
                ErrorHandling.ErrorHandler.Handle(e, ErrorHandling.LogLevel.Error);
                return UpdateResult.HttpError;
            }

            // Null Balrog response indicates no new update
            if (balrogResponse == null)
            {
                return UpdateResult.SuccessNoUpdate;
            }

            var randomBytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(randomBytes);
            }

            // TODO: Is Path.GetTempPath() an okay place to put our files? Would C:\Windows\Temp be safer from TOCTOU attacks?
            var fileName = Path.Combine(Path.GetTempPath(), BitConverter.ToString(randomBytes).Replace("-", string.Empty).ToLower());

            using (var fileStream = new FileStream(fileName, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // Delete file upon reboot
                Windows.Kernel32.MoveFileEx(fileName, null, Windows.MoveFileFlags.DelayUntilReboot);

                // Validate whether we support the Balrog suggested MSI file file hash
                if (!FileHashAlgorithms.ContainsKey(balrogResponse.HashFunction))
                {
                    ErrorHandling.ErrorHandler.Handle("Unsupported MSI file hash.", ErrorHandling.LogLevel.Error);
                    File.Delete(fileName);
                    return UpdateResult.InvalidSignature;
                }

                // Computes the hash of the file as we write to the file
                using (var computedHash = HashAlgorithm.Create(FileHashAlgorithms[balrogResponse.HashFunction].ToString()))
                {
                    using (var cryptoStream = new CryptoStream(fileStream, computedHash, CryptoStreamMode.Write))
                    {
                        var downloadStatus = UpdateDownloadResult.GeneralFailure;

                        try
                        {
                            downloadStatus = await DownloadAndComputeHashAsync(balrogResponse.MsiUrl, cryptoStream);

                            // Clean up data streams
                            cryptoStream.FlushFinalBlock();
                            fileStream.Close();
                        }
                        catch (HttpRequestException e)
                        {
                            ErrorHandling.ErrorHandler.Handle(string.Concat("Update MSI file download error: ", e.Message), ErrorHandling.LogLevel.Error);
                            File.Delete(fileName);
                            return UpdateResult.HttpError;
                        }

                        switch (downloadStatus)
                        {
                            case UpdateDownloadResult.GeneralFailure:
                                ErrorHandling.ErrorHandler.Handle("Could not download MSI update file.", ErrorHandling.LogLevel.Error);
                                File.Delete(fileName);
                                return UpdateResult.GeneralFailure;

                            case UpdateDownloadResult.Georestricted:
                                ErrorHandling.ErrorHandler.Handle("Could not download MSI update file, georestrictions enforced.", ErrorHandling.LogLevel.Error);
                                File.Delete(fileName);
                                return UpdateResult.Georestricted;

                            case UpdateDownloadResult.Success:
                                ErrorHandling.ErrorHandler.Handle("Successfully downloaded MSI update file.", ErrorHandling.LogLevel.Info);
                                break;
                        }
                    }

                    // SHA512 conversion to byte[]
                    var responseHash = Enumerable.Range(0, balrogResponse.HashValue.Length).Where(x => x % 2 == 0).Select(x => Convert.ToByte(balrogResponse.HashValue.Substring(x, 2), 16)).ToArray();

                    // Verify the hash of the update msi file
                    if (!computedHash.Hash.SequenceEqual(responseHash))
                    {
                        // If the hash comparison fails, delete the file
                        File.Delete(fileName);
                        return UpdateResult.InvalidSignature;
                    }

                    // Send a disconnect command to the tunnel if the VPN is not already turned off
                    if (Manager.MainWindowViewModel.Status != Models.ConnectionState.Unprotected)
                    {
                        Manager.Tunnel.Disconnect();
                    }

                    var waitUntilDisconnected = Task.Run(async () =>
                    {
                        while (Manager.MainWindowViewModel.Status != Models.ConnectionState.Unprotected)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1));
                        }
                    });

                    // Wait until the VPN has been disconnected or timeout is reached
                    if (waitUntilDisconnected != await Task.WhenAny(waitUntilDisconnected, Task.Delay(DisconnectTimeout)))
                    {
                        File.Delete(fileName);
                        return UpdateResult.DisconnectTimeout;
                    }

                    ErrorHandling.ErrorHandler.WriteToLog("Running MSI update...", ErrorHandling.LogLevel.Info);

                    var msiUpdateLaunch = LaunchUpdatedApplication(fileName);
                    if (!msiUpdateLaunch)
                    {
                        File.Delete(fileName);
                        return UpdateResult.RunFailure;
                    }

                    return UpdateResult.Success;
                }
            }
        }

        private static async Task<UpdateDownloadResult> DownloadAndComputeHashAsync(string msiUrl, CryptoStream cryptoStream)
        {
            using (var response = await UpdateHttpClient.QueryWithRetryAsync(msiUrl, MaxHttpRetries))
            {
                if (!response.IsSuccessStatusCode)
                {
                    // Georestriction check
                    if (response.StatusCode == (System.Net.HttpStatusCode)451)
                    {
                        ErrorHandling.ErrorHandler.Handle(string.Concat("Error code received while downloading MSI update: ", response.StatusCode), ErrorHandling.LogLevel.Error);
                        return UpdateDownloadResult.Georestricted;
                    }

                    ErrorHandling.ErrorHandler.Handle(string.Concat("Error code received while downloading MSI update: ", response.StatusCode), ErrorHandling.LogLevel.Error);
                    return UpdateDownloadResult.GeneralFailure;
                }

                // Copy response content to newly created update file
                await response.Content.CopyToAsync(cryptoStream);
            }

            return UpdateDownloadResult.Success;
        }

        private static bool LaunchUpdatedApplication(string fileName)
        {
            try
            {
                using (var msiProcess = new Process())
                {
                    msiProcess.StartInfo.WorkingDirectory = Path.GetDirectoryName(fileName);
                    msiProcess.StartInfo.FileName = "msiexec.exe";

                    // Runs the MSI installation in basic GUI mode
                    msiProcess.StartInfo.Arguments = string.Format("/qb!- REBOOT=ReallySuppress /i {0}", Path.GetFileName(fileName));

                    if (!msiProcess.Start())
                    {
                        return false;
                    }

                    msiProcess.WaitForExit();
                    return msiProcess.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
