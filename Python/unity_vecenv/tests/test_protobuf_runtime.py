from unity_vecenv.protobuf_gen.communication_pb2 import InitializeEnvironments


def test_generated_protobuf_round_trip():
    message = InitializeEnvironments(requestedNumberOfEnvs=8)
    decoded = InitializeEnvironments.FromString(message.SerializeToString())

    assert decoded.requestedNumberOfEnvs == 8
