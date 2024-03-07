using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace WalletWasabi.JsonConverters.Collections;

public class HashSetUint256JsonConverter : JsonConverter<HashSet<uint256>>
{
	public override HashSet<uint256>? ReadJson(JsonReader reader, Type objectType, HashSet<uint256>? existingValue, bool hasExistingValue, JsonSerializer serializer)
	{
		var stringValue = reader.Value as string;

		return new HashSet<uint256>() { new uint256(stringValue) };
	}

	public override void WriteJson(JsonWriter writer, HashSet<uint256>? value, JsonSerializer serializer)
	{
		HashSet<uint256> hashSet = value ?? throw new JsonException();
		JObject jo = new JObject(hashSet.Select(s => new JProperty(s.ToString(), s)));
		jo.WriteTo(writer);
	}
}
