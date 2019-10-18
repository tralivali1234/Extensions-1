// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.Models;

namespace Microsoft.Extensions.Configuration.AzureKeyVault
{
    /// <summary>
    /// An AzureKeyVault based <see cref="ConfigurationProvider"/>.
    /// </summary>
    internal class AzureKeyVaultConfigurationProvider : ConfigurationProvider, IDisposable
    {
        private readonly TimeSpan? _reloadInterval;
        private readonly IKeyVaultClient _client;
        private readonly string _vault;
        private readonly IKeyVaultSecretManager _manager;
        private Dictionary<string, LoadedSecret> _loadedSecrets;
        private Task _pollingTask;
        private readonly CancellationTokenSource _cancellationToken;

        /// <summary>
        /// Creates a new instance of <see cref="AzureKeyVaultConfigurationProvider"/>.
        /// </summary>
        /// <param name="client">The <see cref="KeyVaultClient"/> to use for retrieving values.</param>
        /// <param name="vault">Azure KeyVault uri.</param>
        /// <param name="manager">The <see cref="IKeyVaultSecretManager"/> to use in managing values.</param>
        /// <param name="reloadInterval">The timespan to wait in between each attempt at polling the Azure KeyVault for changes. Default is null which indicates no reloading.</param>
        public AzureKeyVaultConfigurationProvider(IKeyVaultClient client, string vault, IKeyVaultSecretManager manager, TimeSpan? reloadInterval = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _vault = vault ?? throw new ArgumentNullException(nameof(vault));
            _manager = manager ?? throw new ArgumentNullException(nameof(manager));
            if (reloadInterval != null && reloadInterval.Value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(reloadInterval), reloadInterval, nameof(reloadInterval) + " must be positive.");
            }

            _pollingTask = null;
            _cancellationToken = new CancellationTokenSource();
            _reloadInterval = reloadInterval;
        }

        /// <summary>
        /// Load secrets into this provider.
        /// </summary>
        public override void Load() => LoadAsync().ConfigureAwait(false).GetAwaiter().GetResult();

        private async Task PollForSecretChangesAsync()
        {
            while (!_cancellationToken.IsCancellationRequested)
            {
                await WaitForReload();
                try
                {
                    await LoadAsync();
                }
                catch (Exception)
                {
                    // Ignore
                }
            }
        }

        protected virtual async Task WaitForReload()
        {
            await Task.Delay(_reloadInterval.Value, _cancellationToken.Token);
        }

        private async Task LoadAsync()
        {
            var secretPage = await _client.GetSecretsAsync(_vault).ConfigureAwait(false);
            var allSecrets = new List<SecretItem>(secretPage.Count());
            do
            {
                allSecrets.AddRange(secretPage.ToList());
                secretPage = secretPage.NextPageLink != null ?
                    await _client.GetSecretsNextAsync(secretPage.NextPageLink).ConfigureAwait(false) :
                     null;
            } while (secretPage != null);

            var tasks = new List<Task<SecretBundle>>();
            var newLoadedSecrets = new Dictionary<string, LoadedSecret>();
            var oldLoadedSecrets = Interlocked.Exchange(ref _loadedSecrets, null);

            foreach (var secret in allSecrets)
            {
                if (!_manager.Load(secret) || secret.Attributes?.Enabled != true)
                {
                    continue;
                }

                var secretId = secret.Identifier.BaseIdentifier;
                if (oldLoadedSecrets != null &&
                    oldLoadedSecrets.TryGetValue(secretId, out var existingSecret) &&
                    existingSecret.IsUpToDate(secret.Attributes.Updated))
                {
                    oldLoadedSecrets.Remove(secretId);
                    newLoadedSecrets.Add(secretId, existingSecret);
                }
                else
                {
                    tasks.Add(_client.GetSecretAsync(secretId));
                }
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var task in tasks)
            {
                var secretBundle = task.Result;
                newLoadedSecrets.Add(secretBundle.SecretIdentifier.BaseIdentifier, new LoadedSecret(_manager.GetKey(secretBundle), secretBundle.Value, secretBundle.Attributes.Updated));
            }

            _loadedSecrets = newLoadedSecrets;

            // Reload is needed if we are loading secrets that were not loaded before or
            // secret that was loaded previously is not available anymore
            if (tasks.Any() || oldLoadedSecrets?.Any() == true)
            {
                SetData(_loadedSecrets, fireToken: oldLoadedSecrets != null);
            }

            // schedule a polling task only if none exists and a valid delay is specified
            if (_pollingTask == null && _reloadInterval != null)
            {
                _pollingTask = PollForSecretChangesAsync();
            }
        }

        private void SetData(Dictionary<string, LoadedSecret> loadedSecrets, bool fireToken)
        {
            var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var secretItem in loadedSecrets)
            {
                data.Add(secretItem.Value.Key, secretItem.Value.Value);
            }

            Data = data;
            if (fireToken)
            {
                OnReload();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _cancellationToken.Cancel();
        }

        private readonly struct LoadedSecret
        {
            public LoadedSecret(string key, string value, DateTime? updated)
            {
                Key = key;
                Value = value;
                Updated = updated;
            }

            public string Key { get; }
            public string Value { get; }
            public DateTime? Updated { get; }

            public bool IsUpToDate(DateTime? updated)
            {
                if (updated.HasValue != Updated.HasValue)
                {
                    return false;
                }

                return updated.GetValueOrDefault() == Updated.GetValueOrDefault();
            }
        }
    }
}
