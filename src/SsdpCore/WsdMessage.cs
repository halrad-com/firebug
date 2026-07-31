using System;

namespace SsdpCore
{
    /// <summary>
    /// WS-Discovery SOAP message builders and parsers.
    /// All XML is string interpolation — these are fixed-structure templates.
    /// </summary>
    public static class WsdMessage
    {
        /// <summary>
        /// Build a Hello message (multicast announcement on startup).
        /// </summary>
        public static string BuildHello(string uuid, string xAddrs, long instanceId, long messageNumber)
        {
            var messageId = $"urn:uuid:{Guid.NewGuid()}";
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""{WsdConstants.NsSoap}"" xmlns:wsa=""{WsdConstants.NsWsa}"" xmlns:wsd=""{WsdConstants.NsWsd}"" xmlns:wsdp=""{WsdConstants.NsWsdp}"">
  <soap:Header>
    <wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>
    <wsa:Action>{WsdConstants.ActionHello}</wsa:Action>
    <wsa:MessageID>{messageId}</wsa:MessageID>
    <wsd:AppSequence InstanceId=""{instanceId}"" MessageNumber=""{messageNumber}"" />
  </soap:Header>
  <soap:Body>
    <wsd:Hello>
      <wsa:EndpointReference>
        <wsa:Address>urn:uuid:{uuid}</wsa:Address>
      </wsa:EndpointReference>
      <wsd:Types>wsdp:Device</wsd:Types>
      <wsd:XAddrs>{xAddrs}</wsd:XAddrs>
      <wsd:MetadataVersion>{WsdConstants.MetadataVersion}</wsd:MetadataVersion>
    </wsd:Hello>
  </soap:Body>
</soap:Envelope>";
        }

        /// <summary>
        /// Build a Bye message (multicast departure notice on shutdown).
        /// </summary>
        public static string BuildBye(string uuid, long instanceId, long messageNumber)
        {
            var messageId = $"urn:uuid:{Guid.NewGuid()}";
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""{WsdConstants.NsSoap}"" xmlns:wsa=""{WsdConstants.NsWsa}"" xmlns:wsd=""{WsdConstants.NsWsd}"">
  <soap:Header>
    <wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>
    <wsa:Action>{WsdConstants.ActionBye}</wsa:Action>
    <wsa:MessageID>{messageId}</wsa:MessageID>
    <wsd:AppSequence InstanceId=""{instanceId}"" MessageNumber=""{messageNumber}"" />
  </soap:Header>
  <soap:Body>
    <wsd:Bye>
      <wsa:EndpointReference>
        <wsa:Address>urn:uuid:{uuid}</wsa:Address>
      </wsa:EndpointReference>
    </wsd:Bye>
  </soap:Body>
</soap:Envelope>";
        }

        /// <summary>
        /// Build a ProbeMatch message (unicast reply to a Probe).
        /// </summary>
        public static string BuildProbeMatch(string uuid, string xAddrs, long instanceId, long messageNumber, string relatesToId)
        {
            var messageId = $"urn:uuid:{Guid.NewGuid()}";
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""{WsdConstants.NsSoap}"" xmlns:wsa=""{WsdConstants.NsWsa}"" xmlns:wsd=""{WsdConstants.NsWsd}"" xmlns:wsdp=""{WsdConstants.NsWsdp}"">
  <soap:Header>
    <wsa:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</wsa:To>
    <wsa:Action>{WsdConstants.ActionProbeMatches}</wsa:Action>
    <wsa:MessageID>{messageId}</wsa:MessageID>
    <wsa:RelatesTo>{relatesToId}</wsa:RelatesTo>
    <wsd:AppSequence InstanceId=""{instanceId}"" MessageNumber=""{messageNumber}"" />
  </soap:Header>
  <soap:Body>
    <wsd:ProbeMatches>
      <wsd:ProbeMatch>
        <wsa:EndpointReference>
          <wsa:Address>urn:uuid:{uuid}</wsa:Address>
        </wsa:EndpointReference>
        <wsd:Types>wsdp:Device</wsd:Types>
        <wsd:XAddrs>{xAddrs}</wsd:XAddrs>
        <wsd:MetadataVersion>{WsdConstants.MetadataVersion}</wsd:MetadataVersion>
      </wsd:ProbeMatch>
    </wsd:ProbeMatches>
  </soap:Body>
</soap:Envelope>";
        }

        /// <summary>
        /// Build a GetResponse message (HTTP metadata exchange response).
        /// This is what Windows requests after discovering the device via Probe/ProbeMatch.
        /// The PresentationUrl becomes the "Device webpage" link in Explorer.
        /// </summary>
        public static string BuildGetResponse(string relatesToId, string uuid, string friendlyName, string manufacturer, string modelName, string presentationUrl)
        {
            var messageId = $"urn:uuid:{Guid.NewGuid()}";
            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""{WsdConstants.NsSoap}"" xmlns:wsa=""{WsdConstants.NsWsa}"" xmlns:wsx=""{WsdConstants.NsWsx}"" xmlns:wsdp=""{WsdConstants.NsWsdp}"">
  <soap:Header>
    <wsa:To>http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</wsa:To>
    <wsa:Action>{WsdConstants.ActionGetResponse}</wsa:Action>
    <wsa:MessageID>{messageId}</wsa:MessageID>
    <wsa:RelatesTo>{relatesToId}</wsa:RelatesTo>
  </soap:Header>
  <soap:Body>
    <wsx:Metadata>
      <wsx:MetadataSection Dialect=""{WsdConstants.NsWsdp}/ThisDevice"">
        <wsdp:ThisDevice>
          <wsdp:FriendlyName>{friendlyName}</wsdp:FriendlyName>
          <wsdp:FirmwareVersion>0.5</wsdp:FirmwareVersion>
          <wsdp:SerialNumber>urn:uuid:{uuid}</wsdp:SerialNumber>
        </wsdp:ThisDevice>
      </wsx:MetadataSection>
      <wsx:MetadataSection Dialect=""{WsdConstants.NsWsdp}/ThisModel"">
        <wsdp:ThisModel>
          <wsdp:Manufacturer>{manufacturer}</wsdp:Manufacturer>
          <wsdp:ModelName>{modelName}</wsdp:ModelName>
          <wsdp:PresentationUrl>{presentationUrl}</wsdp:PresentationUrl>
        </wsdp:ThisModel>
      </wsx:MetadataSection>
      <wsx:MetadataSection Dialect=""{WsdConstants.NsWsdp}/Relationship"">
        <wsdp:Relationship Type=""{WsdConstants.NsWsdp}/host"">
          <wsdp:Host>
            <wsa:EndpointReference>
              <wsa:Address>urn:uuid:{uuid}</wsa:Address>
            </wsa:EndpointReference>
            <wsdp:Types>wsdp:Device</wsdp:Types>
          </wsdp:Host>
        </wsdp:Relationship>
      </wsx:MetadataSection>
    </wsx:Metadata>
  </soap:Body>
</soap:Envelope>";
        }

        /// <summary>
        /// Check if an incoming UDP message is a WS-Discovery Probe.
        /// </summary>
        public static bool IsProbe(string message)
        {
            return !string.IsNullOrEmpty(message) && message.Contains(WsdConstants.ActionProbe);
        }

        /// <summary>
        /// Check if an incoming HTTP message is a WS-Transfer Get request.
        /// </summary>
        public static bool IsGetRequest(string message)
        {
            return !string.IsNullOrEmpty(message) && message.Contains(WsdConstants.ActionGet);
        }

        /// <summary>
        /// Extract wsa:MessageID from a SOAP message for use in RelatesTo.
        /// </summary>
        public static string ParseMessageId(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;

            // Look for <wsa:MessageID>...</wsa:MessageID>
            const string startTag = "<wsa:MessageID>";
            const string endTag = "</wsa:MessageID>";

            var start = message.IndexOf(startTag, StringComparison.Ordinal);
            if (start < 0) return null;

            start += startTag.Length;
            var end = message.IndexOf(endTag, start, StringComparison.Ordinal);
            if (end < 0) return null;

            return message.Substring(start, end - start).Trim();
        }
    }
}
