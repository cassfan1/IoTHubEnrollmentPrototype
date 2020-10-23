using Microsoft.Azure.Devices;
using Microsoft.Azure.Devices.Provisioning.Client;
using Microsoft.Azure.Devices.Provisioning.Client.Transport;
using Microsoft.Azure.Devices.Provisioning.Service;
using Microsoft.Azure.Devices.Shared;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace IoTHubEnrollmentPrototype
{
  class Program
  {
    private const string ProvisionConnString = "";
    private const string IoTHubConnectionString = "";
    private const string GlobalDeviceEndpoint = "";
    private const string IdScope = "";
    private const string RegisteredId = "dps-iot-demo-001";

    static async Task Main(string[] args)
    {
      // enroll to IoTHub
      //var response = await EnrollToIoTHubAsync(RegisteredId);
      //Console.WriteLine(response.PrimaryKey);
      //Console.WriteLine(response.SecondKey);

      // update twins
      //await UpdateDesiredProperties(RegisteredId);
    }

    #region Enroll to IoTHub

    private static async Task<IoTHubResponse> EnrollToIoTHubAsync(string beckmanConnectId)
    {
      try
      {
        var primaryKey = ComputeDerivedSymmetricKey(Guid.NewGuid().ToString());
        var secondKey = ComputeDerivedSymmetricKey(Guid.NewGuid().ToString());

        // enroll to dps
        var enrollToDpsResult = await EnrollToDpsAsync(primaryKey, secondKey, beckmanConnectId, ProvisionConnString);
        if (!string.IsNullOrWhiteSpace(enrollToDpsResult.RegistrationId))
        {
          // register in DPS, but not assign to IoTHub
          if (enrollToDpsResult.RegistrationState == null || string.IsNullOrWhiteSpace(enrollToDpsResult.RegistrationState.AssignedHub))
          {
            // assign to IoTHub

            // TODO
            // If DPS success and IotHub failed, when customer register next time, will cause different keys for DPS and IoT
            // So need to get dps attestation in order to keep DPS and IoT have same keys.
            // For now, the DPS SDK cannot get symmetric keys text, consider to save in database or failure mode.

            var registerToIoTHub = await RegisterToIoTHubAsync(primaryKey, secondKey, beckmanConnectId);
            if (!string.IsNullOrWhiteSpace(registerToIoTHub.AssignedHub))
            {
              // get device connection string returns to sync client
              var devicePrimaryConnectionString = $"HostName={registerToIoTHub.AssignedHub};DeviceId={beckmanConnectId};SharedAccessKey={primaryKey}";
              var deviceSecondConnectionString = $"HostName={registerToIoTHub.AssignedHub};DeviceId={beckmanConnectId};SharedAccessKey={secondKey}";

              var response = new IoTHubResponse
              {
                PrimaryKey = devicePrimaryConnectionString,
                SecondKey = deviceSecondConnectionString
              };

              Console.WriteLine("Enroll successfully!");
              return response;
            }

            Console.WriteLine("Assign to IoTHub failed");
          }
          else
          {
            Console.Write("Enroll successfully.");
          }
        }
        else
        {
          Console.WriteLine("Enroll to dps failed");
        }
      }
      catch (Exception ex)
      {
        Console.WriteLine($"Exceptions: {ex}");
      }

      return null;
    }

    private static async Task<IndividualEnrollment> EnrollToDpsAsync(string primaryKey, string secondKey, string registeredId, string provisionConnString)
    {
      using var provisioningServiceClient = ProvisioningServiceClient.CreateFromConnectionString(ProvisionConnString);
      IndividualEnrollment device = null;

      try
      {
        device = await provisioningServiceClient.GetIndividualEnrollmentAsync(registeredId).ConfigureAwait(false);
      }
      catch (ProvisioningServiceClientException ex)
      {
        // device is not existed
      }

      // if device is not existed, create device in DPS
      if (device == null)
      {
        Attestation attestation = new SymmetricKeyAttestation(primaryKey, secondKey);
        var individualEnrollment =
          new IndividualEnrollment(
            registeredId,
            attestation)
          {
            // TODO check device id region and set region IoTHub service host name
            IotHubHostName = "connect-iothub-dev.azure-devices.net"
          };

        device = await provisioningServiceClient.CreateOrUpdateIndividualEnrollmentAsync(individualEnrollment);
      }

      return device;
    }

    public static async Task<DeviceRegistrationResult> RegisterToIoTHubAsync(string primaryKey, string secondKey, string registeredId)
    {
      using var security = new SecurityProviderSymmetricKey(registeredId, primaryKey, secondKey);
      using var transport = new ProvisioningTransportHandlerHttp();
      var provClient = ProvisioningDeviceClient.Create(
        GlobalDeviceEndpoint,
        IdScope,
        security,
        transport);

      // register into IoTHub
      var result = await provClient.RegisterAsync();
      Console.WriteLine($"ProvisioningClient AssignedHub: {result.AssignedHub}; DeviceID: {result.DeviceId}");
      return result;
    }

    private static async Task<AttestationMechanism> GetIndividualAttestationAsync(string registeredId)
    {
      using var provisioningServiceClient = ProvisioningServiceClient.CreateFromConnectionString(ProvisionConnString);
      var attestationMechanism = await provisioningServiceClient.GetIndividualEnrollmentAttestationAsync(registeredId);
      return attestationMechanism;
    }

    #endregion

    #region Update device keys

    private static async Task<IoTHubResponse> UpdateExistDeviceKeysAsync(string beckmanConnectId, string iotHubConnectionString)
    {
      using var provisioningServiceClient = ProvisioningServiceClient.CreateFromConnectionString(ProvisionConnString);
      var individualEnrollmentResult = await provisioningServiceClient.GetIndividualEnrollmentAsync(beckmanConnectId);

      var primaryKey = ComputeDerivedSymmetricKey(Guid.NewGuid().ToString());
      var secondKey = ComputeDerivedSymmetricKey(Guid.NewGuid().ToString());

      // update dps keys
      Attestation attestation = new SymmetricKeyAttestation(primaryKey, secondKey);
      individualEnrollmentResult.Attestation = attestation;

      var updateDpsResult = await provisioningServiceClient.CreateOrUpdateIndividualEnrollmentAsync(individualEnrollmentResult);
      if (updateDpsResult.RegistrationId == beckmanConnectId)
      {
        // update IoTHub keys (rollback or errors when failed)
        var registryManager = RegistryManager.CreateFromConnectionString(iotHubConnectionString);
        var device = await registryManager.GetDeviceAsync(beckmanConnectId);

        var newDevice = new Device(beckmanConnectId)
        {
          ETag = device.ETag,
          Authentication = new AuthenticationMechanism
          {
            SymmetricKey = new SymmetricKey
            {
              PrimaryKey = primaryKey,
              SecondaryKey = secondKey
            }
          }
        };

        var deviceResponse = await registryManager.UpdateDeviceAsync(newDevice);
        if (string.IsNullOrWhiteSpace(deviceResponse.Id))
        {
          var response = new IoTHubResponse
          {
            PrimaryKey = primaryKey,
            SecondKey = secondKey
          };

          return response;
        }
      }

      return null;
    }

    #endregion

    #region Utils

    public static string ComputeDerivedSymmetricKey(string key)
    {
      using var sha256Hash = SHA256.Create();
      var hash = GetHash(sha256Hash, key);
      return hash;
    }

    private static string GetHash(HashAlgorithm hashAlgorithm, string input)
    {
      var data = hashAlgorithm.ComputeHash(Encoding.UTF8.GetBytes(input));
      var sBuilder = new StringBuilder();
      foreach (var t in data)
      {
        sBuilder.Append(t.ToString("x2"));
      }
      return sBuilder.ToString();
    }

    #endregion

    #region Update twins

    public static async Task UpdateDesiredProperties(string deviceId)
    {
      var registryManager = RegistryManager.CreateFromConnectionString(IoTHubConnectionString);
      var twin = await registryManager.GetTwinAsync(deviceId).ConfigureAwait(false);

      var patch =
        @"{
                properties: {
                    desired: {
                      updater: '2.0.5',
                      link: 'https://xxxx'
                    }
                }
            }";

      await registryManager.UpdateTwinAsync(twin.DeviceId, patch, twin.ETag).ConfigureAwait(false);
    }

    #endregion
  }

  public class IoTHubResponse
  {
    public string PrimaryKey { get; set; }
    public string SecondKey { get; set; }
  }
}
