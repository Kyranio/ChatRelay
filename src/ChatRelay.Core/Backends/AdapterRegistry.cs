using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChatRelay.Logging;

namespace ChatRelay.Backends
{
    /// <summary>
    /// Discovers installed adapters and aggregates their model lists into the
    /// flat collection the dropdown binds to. Probing is concurrent; one slow
    /// or unreachable adapter doesn't block the others. Results are cached for
    /// the lifetime of the tool window — call <see cref="RefreshAsync"/> to
    /// re-probe (e.g. after the user installs Ollama).
    /// </summary>
    public class AdapterRegistry
    {
        // Every adapter that can potentially be available. Probing filters
        // this down to the ones we'll actually expose.
        private readonly List<IAiAdapter> _all;

        private readonly object _lock = new object();
        private List<IAiAdapter> _available = new List<IAiAdapter>();
        private List<AiModel> _models = new List<AiModel>();

        /// <summary>Raised after <see cref="RefreshAsync"/> completes. UI thread is not guaranteed.</summary>
        public event EventHandler? Changed;

        public AdapterRegistry() { _all = new List<IAiAdapter>(); }

        public void Register(IAiAdapter adapter) => _all.Add(adapter);

        public IReadOnlyList<IAiAdapter> AvailableAdapters
        {
            get { lock (_lock) return _available.ToList(); }
        }

        public IReadOnlyList<AiModel> Models
        {
            get { lock (_lock) return _models.ToList(); }
        }

        public IAiAdapter? GetById(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock)
                return _available.FirstOrDefault(a =>
                    string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>First available adapter, or null. Used as a fallback when a persisted session's adapter is gone.</summary>
        public IAiAdapter? FirstAvailable()
        {
            lock (_lock) return _available.FirstOrDefault();
        }

        /// <summary>
        /// Run all probes in parallel, then enumerate models on the survivors.
        /// Always completes — individual adapter failures are logged and
        /// silently drop that adapter for this round.
        /// </summary>
        public async Task RefreshAsync(CancellationToken ct = default)
        {
            ExtensionLogger.Info("registry", $"Probing {_all.Count} adapters");

            var probeTasks = _all.Select(async a =>
            {
                try
                {
                    var ok = await a.IsAvailableAsync(ct).ConfigureAwait(false);
                    return (adapter: a, available: ok);
                }
                catch (Exception ex)
                {
                    ExtensionLogger.Warn("registry", $"Probe threw for {a.Id}", ex);
                    return (adapter: a, available: false);
                }
            }).ToArray();

            var probes = await Task.WhenAll(probeTasks).ConfigureAwait(false);
            var survivors = probes.Where(p => p.available).Select(p => p.adapter).ToList();

            // Enumerate models in parallel too.
            var modelTasks = survivors.Select(async a =>
            {
                try
                {
                    var list = await a.ListModelsAsync(ct).ConfigureAwait(false);
                    return list ?? new List<AiModel>();
                }
                catch (Exception ex)
                {
                    ExtensionLogger.Warn("registry", $"Model list threw for {a.Id}", ex);
                    return (IReadOnlyList<AiModel>)new List<AiModel>();
                }
            }).ToArray();

            var modelLists = await Task.WhenAll(modelTasks).ConfigureAwait(false);
            var flat = modelLists.SelectMany(x => x).ToList();

            lock (_lock)
            {
                _available = survivors;
                _models = flat;
            }

            ExtensionLogger.Info("registry",
                $"Done: {survivors.Count} adapter(s), {flat.Count} model(s) total " +
                $"({string.Join(", ", survivors.Select(a => a.Id))})");

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
