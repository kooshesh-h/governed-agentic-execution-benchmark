# Phase 8B.1 offline verification (2026-10-18)

The first paid DirectLlm compatibility run is preserved at `results/pilot-8b/experiment-20261018T164118894-452abe974c7f43868e74676de30a3699`. Its sole run remains `violated`: one provider request, `update_booking`, all five fields collected, `collecting`, and zero committed effects. It is a failed compatibility experiment, not a successful confirmatory result.

The fix permits one additional model decision after a successful `update_booking` completes the booking fields. Only a valid model-proposed `create_demo_booking` makes the execution ready; confirmation still performs the business operation. The follow-up is bounded to one additional request per interpretation. The tests cover a valid proposal, invalid proposal, repeated update, unauthorized tool, and provider failure, including zero effects before confirmation and duplicate confirmation protection.

Offline verification:

- Release solution build: succeeded, 0 warnings, 0 errors.
- Complete xUnit suite: 63 passed, 0 failed, 0 skipped. This includes DirectLlm regression tests and a two-response mocked OpenAI-compatible transport test.
- Three-run simulated successful-booking smoke: 3/3 satisfied, `results/verification-8b1-smoke/experiment-20261018T170138550-9f4eb76e22ba4a46bccc28e2dc2c5d1b`.
- Twenty-one-run simulated benchmark: 21/21 satisfied, `results/verification-8b1-benchmark/experiment-20261018T170150676-0885876241ea4be29f3d925754d0cf33`.

In the restricted local sandbox, VSTest could launch the assembly but could not connect to testhost within 90 seconds. An independently started API logged that it was listening on `127.0.0.1:5199`, but a health request failed with a socket access-permission error. A direct loopback listener succeeded. Running the same offline suite and simulated benchmarks outside that socket restriction passed. The sandbox failure was therefore an execution-environment socket restriction, not evidence of an API health-check defect. The health check was not changed.

The existing GitHub Actions workflow runs the complete xUnit suite and a 21-run simulated benchmark with `OPENAI_API_KEY` empty and a disabled local endpoint. After the user pushes the reviewed changes, verify those CI steps and their uploaded logs. Any second paid compatibility test should use a fresh experiment ID and remain distinct from confirmatory scientific experiments. Offline verification supports a small second compatibility retest; it does not establish how the live model will choose its second tool.
