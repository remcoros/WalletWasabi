using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using NBitcoin;
using WalletWasabi.Backend.Models;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Mempool;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Logging;
using WalletWasabi.Microservices;
using WalletWasabi.Models;
using WalletWasabi.Stores;

namespace WalletWasabi.Wallets;

public class WalletFilterProcessor : BackgroundService
{
	private const int MaxNumberFiltersInMemory = 1000;
	
	private static readonly Comparer<Priority> Comparer = Comparer<Priority>.Create(
		(x, y) =>
		{
			// Turbo and Complete have higher priority over NonTurbo.
			if (x.SyncType != SyncType.NonTurbo && y.SyncType == SyncType.NonTurbo)
            {
            	return -1;
            }
			if (y.SyncType != SyncType.NonTurbo && x.SyncType == SyncType.NonTurbo)
			{
				return 1;
			}

			// Higher height have higher priority.
			return -y.Height.CompareTo(x.Height);
		});
	
	public WalletFilterProcessor(KeyManager keyManager, BitcoinStore bitcoinStore, TransactionProcessor transactionProcessor, IBlockProvider blockProvider)
	{
		KeyManager = keyManager;
		BitcoinStore = bitcoinStore;
		TransactionProcessor = transactionProcessor;
		BlockProvider = blockProvider;
	}

	private PriorityQueue<SyncRequest, Priority> SynchronizationRequests { get; } = new(Comparer);
	private SemaphoreSlim SynchronizationRequestsSemaphore { get; } = new(initialCount: 0);

	/// <remarks>Guards <see cref="SynchronizationRequests"/>.</remarks>
	private object SynchronizationRequestsLock { get; } = new();

	private KeyManager KeyManager { get; }
	private BitcoinStore BitcoinStore { get; }
	private TransactionProcessor TransactionProcessor { get; }
	private IBlockProvider BlockProvider { get; }
	public FilterModel? LastProcessedFilter { get; private set; }
	private Dictionary<uint, FilterModel> FiltersCache { get; } = new ();

	private void AddNoLock(SyncRequest request)
	{
		Priority priority = new(request.SyncType, request.Height);
		SynchronizationRequests.Enqueue(request, priority);
		SynchronizationRequestsSemaphore.Release(releaseCount: 1);
	}

	public void Remove(uint fromHeight)
	{
		lock (SynchronizationRequestsLock)
		{
			SynchronizationRequests.UnorderedItems
				.Where(item => item.Element.Height >= fromHeight)
				.ToList()
				.ForEach(item => item.Element.DoNotProcess = true);
		}
	}

	public async Task ProcessAsync(uint toHeight, IEnumerable<SyncType> syncTypes)
	{
		var tasks = syncTypes.Select(syncType => ProcessAsync(toHeight, syncType)).ToList();
		await Task.WhenAll(tasks).ConfigureAwait(false);
	}
	
	public async Task ProcessAsync(uint toHeight, SyncType syncType)
	{
		 var currentHeight = (uint)(syncType == SyncType.Turbo ? 
			KeyManager.GetBestTurboSyncHeight() + 1:
			KeyManager.GetBestHeight() + 1);

		List<Task> tasks = new();
		lock (SynchronizationRequestsLock)
		{
			var items = SynchronizationRequests.UnorderedItems;
			foreach (var height in Enumerable.Range((int)currentHeight, (int)(toHeight - currentHeight) + 1))
			{
				if (items.Any(x => x.Element.Height == height && x.Element.SyncType == syncType))
				{
					continue;
				}
				
				var tcs = new TaskCompletionSource();
				AddNoLock(new SyncRequest(syncType, (uint)height, tcs));
				tasks.Add(tcs.Task);
			}
		}

		await Task.WhenAll(tasks).ConfigureAwait(false); // This will throw if a tasks throws.
	}

	/// <inheritdoc />
	/// <summary>Used for filter synchronization.</summary>
	protected override async Task ExecuteAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			await SynchronizationRequestsSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

			SyncRequest? request;
			lock (SynchronizationRequestsLock)
			{
				if (!SynchronizationRequests.TryDequeue(out request, out _))
				{
					continue;
				}
			}

			if (request.DoNotProcess)
			{
				request.Tcs.SetCanceled(CancellationToken.None);
				continue;
			}

			try
			{
				if (!FiltersCache.TryGetValue(request.Height, out var filterToProcess))
				{
					filterToProcess = await UpdateFiltersCacheAndReturnFirstAsync(request.Height, cancellationToken).ConfigureAwait(false);
				}

				await ProcessFilterModelAsync(filterToProcess, request.SyncType, cancellationToken).ConfigureAwait(false);
				request.Tcs.SetResult();
			}
			catch (Exception ex)
			{
				Logger.LogError(ex);
				request.Tcs.SetException(ex);
				lock (SynchronizationRequestsLock)
				{
					// Cancel the remaining tasks before throwing.
					while (SynchronizationRequests.TryDequeue(out request, out _))
					{
						request.Tcs.SetCanceled(CancellationToken.None);
					}

					SynchronizationRequestsSemaphore.Dispose();
					throw;
				}
			}

			if (SynchronizationRequestsSemaphore.CurrentCount == 0)
			{
				FiltersCache.Clear();
			}
		}
	}

	private async Task<FilterModel> UpdateFiltersCacheAndReturnFirstAsync(uint startingHeight, CancellationToken cancellationToken)
	{
		FiltersCache.Clear();
		var filtersBatch = await BitcoinStore.IndexStore.FetchBatchAsync(new Height(startingHeight), MaxNumberFiltersInMemory, cancellationToken).ConfigureAwait(false);
		foreach (var filter in filtersBatch)
		{
			FiltersCache[filter.Header.Height] = filter;
		}

		return filtersBatch.First();
	}
	
	/// <summary>
	/// Return the keys to test against the filter depending on the height of the filter and the type of synchronization.
	/// </summary>
	/// <param name="filterHeight">Height of the filter that needs to be tested.</param>
	/// <param name="syncType">First sync of TurboSync, second one, or complete synchronization.</param>
	/// <returns>Keys to test against this filter</returns>
	/// <seealso href="https://github.com/zkSNACKs/WalletWasabi/issues/10219">TurboSync specification.</seealso>
	private List<byte[]> GetScriptPubKeysToTest(Height filterHeight, SyncType syncType)
	{
		if (syncType == SyncType.Complete)
		{
			return KeyManager.UnsafeGetSynchronizationInfos().Select(x => x.ScriptBytesHdPubKeyPair.ScriptBytes).ToList();
		}

		Func<HdPubKey, bool> stepPredicate = syncType == SyncType.Turbo
			? hdPubKey => hdPubKey.LatestSpendingHeight is null || (Height)hdPubKey.LatestSpendingHeight >= filterHeight
			: hdPubKey => hdPubKey.LatestSpendingHeight is not null && (Height)hdPubKey.LatestSpendingHeight < filterHeight;

		IEnumerable<byte[]> keysToTest = KeyManager.UnsafeGetSynchronizationInfos()
			.Where(x => stepPredicate(x.ScriptBytesHdPubKeyPair.HdPubKey))
			.Select(x => x.ScriptBytesHdPubKeyPair.ScriptBytes);

		return keysToTest.ToList();
	}

	private async Task ProcessFilterModelAsync(FilterModel filter, SyncType syncType, CancellationToken cancel)
	{
		var height = new Height(filter.Header.Height);
		var toTestKeys = GetScriptPubKeysToTest(height, syncType);

		if (toTestKeys.Count > 0)
		{
			bool matchFound = filter.Filter.MatchAny(toTestKeys, filter.FilterKey);

			if (matchFound)
			{
				Block currentBlock = await BlockProvider.GetBlockAsync(filter.Header.BlockHash, cancel).ConfigureAwait(false); // Wait until not downloaded.

				var txsToProcess = new List<SmartTransaction>();
				for (int i = 0; i < currentBlock.Transactions.Count; i++)
				{
					Transaction tx = currentBlock.Transactions[i];
					txsToProcess.Add(new SmartTransaction(tx, height, currentBlock.GetHash(), i, firstSeen: currentBlock.Header.BlockTime, labels: BitcoinStore.MempoolService.TryGetLabel(tx.GetHash())));
				}

				TransactionProcessor.Process(txsToProcess);

				if (syncType == SyncType.Turbo)
				{
					// Only keys in TurboSync subset (external + internal that didn't receive or fully spent coins) were tested, update TurboSyncHeight
					KeyManager.SetBestTurboSyncHeight(height);
				}
				else
				{
					// All keys were tested at this height, update the Height.
					KeyManager.SetBestHeight(height);
				}
			}
		}

		LastProcessedFilter = filter;
	}
	
	private record SyncRequest(SyncType SyncType, uint Height, TaskCompletionSource Tcs)
	{
		public bool DoNotProcess { get; set; }
	}
	private record Priority(SyncType SyncType, uint Height);
}