using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;

namespace WalletWasabi.Blockchain.Analysis
{
	public class BlockchainAnalyzer
	{
		public BlockchainAnalyzer(int privacyLevelThreshold, Money dustThreshold)
		{
			PrivacyLevelThreshold = privacyLevelThreshold;
			DustThreshold = dustThreshold;
		}

		public int PrivacyLevelThreshold { get; }
		public Money DustThreshold { get; }

		/// <summary>
		/// Sets clusters and anonymity sets for related HD public keys.
		/// </summary>
		public void Analyze(SmartTransaction tx)
		{
			var inputCount = tx.Transaction.Inputs.Count;
			var outputCount = tx.Transaction.Outputs.Count;

			var ownInputCount = tx.WalletInputs.Count;
			var ownOutputCount = tx.WalletOutputs.Count;

			var othersOutputCount = outputCount - ownOutputCount;

			if (inputCount == ownInputCount && outputCount == ownOutputCount)
			{
				// If self spend then retain anonset:
				// Reusing pubkey on the input side is good, the punishment happened already.
				var distinctWalletInputPubKeys = tx.WalletInputs.Select(x => x.HdPubKey).ToHashSet();
				var anonset = Intersect(distinctWalletInputPubKeys.Select(x => x.AnonymitySet), 1);
				foreach (var key in tx.WalletInputs.Concat(tx.WalletOutputs).Select(x => x.HdPubKey))
				{
					key.AnonymitySet = anonset;
				}
			}
			else if (inputCount == ownInputCount && othersOutputCount > 0)
			{
				// If all our inputs are ours and there's more than one output that isn't,
				// then we can assume that the persons the money was sent to learnt our inputs.
				// AND if there're outputs that go to someone else,
				// then we can assume that the people learnt our change outputs,
				// or at the very least assume that all the changes in the tx is ours.
				// For example even if the assumed change output is a payment to someone, a blockchain analyzer
				// probably would just assume it's ours and go on with its life.
				foreach (var key in tx.WalletInputs.Concat(tx.WalletOutputs).Select(x => x.HdPubKey))
				{
					key.AnonymitySet = 1;
				}
			}
			else
			{
				var inheritedAnonset = 0;
				// If we provided inputs to the transaction.
				var distinctWalletInputPubKeys = tx.WalletInputs.Select(x => x.HdPubKey).ToHashSet();
				if (ownInputCount > 0)
				{
					// Reusing pubkey on the input side is good, the punishment happened already.
					var distinctWalletInputPubKeyCount = distinctWalletInputPubKeys.Count;
					var pubKeyReuseCount = ownInputCount - distinctWalletInputPubKeyCount;
					var privacyBonus = Intersect(distinctWalletInputPubKeys.Select(x => x.AnonymitySet), (double)distinctWalletInputPubKeyCount / (inputCount - pubKeyReuseCount));

					// If the privacy bonus is <=1 then we are not inheriting any privacy from the inputs.
					var normalizedBonus = privacyBonus - 1;

					// And add that to the base anonset from the tx.
					inheritedAnonset = normalizedBonus;

					foreach (var key in tx.WalletInputs.Select(x => x.HdPubKey))
					{
						key.AnonymitySet = inheritedAnonset;
					}
				}

				foreach (var newCoin in tx.WalletOutputs)
				{
					// Get the anonymity set of i-th output in the transaction.
					var anonset = inheritedAnonset;
					anonset += tx.Transaction.GetAnonymitySet(newCoin.Index);

					// Let's assume the blockchain analyser also participates in the transaction.
					anonset = Math.Max(1, anonset - 1);

					HdPubKey hdPubKey = newCoin.HdPubKey;
					if (hdPubKey.AnonymitySet == HdPubKey.DefaultHighAnonymitySet)
					{
						// If the new coin's HD pubkey haven't been used yet
						// then its anonset haven't been set yet.
						// In that case the acquired anonset does not have to be intersected with the default anonset,
						// so this coin gets the aquired anonset.
						hdPubKey.AnonymitySet = anonset;
					}
					else if (distinctWalletInputPubKeys.Contains(hdPubKey))
					{
						// If it's a reuse of an input's pubkey, then intersection punishment is senseless.
						hdPubKey.AnonymitySet = inheritedAnonset;
					}
					else
					{
						hdPubKey.AnonymitySet = Intersect(new[] { anonset, hdPubKey.AnonymitySet }, 1);
					}
				}
			}

			AnalyzeClusters(tx);
		}

		private void AnalyzeClusters(SmartTransaction tx)
		{
			foreach (var newCoin in tx.WalletOutputs)
			{
				if (newCoin.HdPubKey.AnonymitySet < PrivacyLevelThreshold)
				{
					// Set clusters.
					foreach (var spentCoin in tx.WalletInputs)
					{
						newCoin.HdPubKey.Cluster.Merge(spentCoin.HdPubKey.Cluster);
					}
				}
			}
		}

		/// <param name="coefficient">If larger than 1, then penalty is larger, if smaller than 1 then penalty is smaller.</param>
		private int Intersect(IEnumerable<int> anonsets, double coefficient)
		{
			// Sanity check.
			if (!anonsets.Any())
			{
				return 1;
			}

			// Our smallest anonset is the relevant here, because anonsets cannot grow by intersection punishments.
			var smallestAnon = anonsets.Min();

			// Punish intersection exponentially.
			// If there is only a single anonset then the exponent should be zero to divide by 1 thus retain the input coin anonset.
			var intersectPenalty = Math.Pow(2, anonsets.Count() - 1);
			var intersectionAnonset = smallestAnon / Math.Max(1, intersectPenalty * coefficient);

			// Sanity check.
			var normalizedIntersectionAnonset = Math.Max(1, (int)intersectionAnonset);
			return normalizedIntersectionAnonset;
		}
	}
}
