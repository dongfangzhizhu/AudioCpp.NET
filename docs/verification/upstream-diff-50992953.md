# audio.cpp upstream difference report

- Generated: 2026-11-20T03:15:38.9622788Z
- Repository: https://github.com/dongfangzhizhu/audio.cpp
- Pinned: `4be37c28f4f51c2b191c8587e38debd41c7dd626`
- Candidate: `509929537ae6a673ab20cc2080e69d9e514559c3` (50992953)
- Commits forward: 54
- Changed paths: 563

## Recommended actions

- BLOCK automatic pin update: review and adapt the native shim, then run ABI, native, managed, and model E2E tests.
- Rebuild every supported RID/backend; inspect dynamic dependencies, exported symbols, licenses, SBOM, and portable CPU settings.
- Re-evaluate the compiled model set and capability matrix; add or update model-specific golden tests where affected.
- Map upstream test changes to wrapper regression tests; do not assume passing compilation preserves semantics.

## Commits

- `50992953 sync: merge upstream 29b37ddb into fork`
- `29b37ddb Reduce oversized GGML metadata arena reservations (2) (#1347)`
- `9143b139 Reduce oversized GGML metadata arena reservations (1) (#1346)`
- `f2705114 fish_drama3: keep several encoded reference voices (LRU, exact waveform match) (#1345)`
- `df60b9d3 pocket_tts: apply the config's replace_characters like the reference (#1339)`
- `a9822529 Fix MuLaCover ChordNet allocation for long references (#1344)`
- `5e7273db New Community Model: Granite Speech 5.0 2B (IBM) (#1332)`
- `ee499a1b Reuse Granite5ASR encoder graphs and batch GPU block attention (#1335)`
- `ced4b4b2 馃毃 Introduce a standalone MoE engine with independent placement and cache policies (#1334)`
- `4de392e0 Bound HiFT graph metadata arenas independently of audio length (#1331)`
- `ebd8c15a Reduce CosyVoice3 metadata arenas and add speech speed control (#1330)`
- `749fffec Add Latin support (#1322)`
- `b47cea0e zonos2: continue long-form chunks from earlier audio codes (#1328)`
- `a6e32214 Preserve Japanese text during Chinese-script normalization (#1326)`
- `92b9260a fix(tiny_voca): keep graphs in a bounded session cache and refuse languages other than English (#1324)`
- `6f71a3b8 Fix RMVPE stream lookup with dynamic CUDA backends (#1321)`
- `bf76684a Use shared Mimi decoding for PocketTTS and correct streaming history (#1320)`
- `cfc30b04 Add SoulX-Singer singing synthesis and voice conversion (#1319)`
- `28a6fe7b Refactor PocketTTS Small to use shared Mimi latent decoder runtime (#1318)`
- `71b620de Fix PocketTTS decoder lifetimes across chunk and session reuse (#1314)`
- `87a2fadf Fix CLI precedence for MoE YAML options (#1317)`
- `2d99b765 Add Linux and Windows HIP rocBLAS compatibility packages (#1316)`
- `4c2293a4 feat: detect tensor container by content when the extension is unrecognised (#1315)`
- `5c98ebc1 Add YAML config support to CLI (#1304)`
- `8ac02f32 feat: add tiny-voca, a 4.5M-parameter English TTS model with its own phonemizer (#1311)`
- `2872e36e Refactor Conformer blocks to reuse shared feed-forward modules (#1309)`
- `5e823928 Add ROSVOT singing-note transcription (#1308)`
- `fc3852a2 Add ACE-Step reference-audio conditioning (#1307)`
- `5ec688c6 Verify Windows Xet downloads after reconstruction (#1301)`
- `5158d437 omnivoice: review-round fixes for the rknn_zeroop weight cache (#1303)`
- `da95c6a0 omnivoice: harden the rknn_zeroop packed-weight disk cache (#1302)`
- `89bccbea omnivoice: run the generator on the vendor zero-op matmul API (rknn_zeroop) (#1297)`
- `83b1354b Add M5-TTS voice cloning, preset synthesis and streaming (#1298)`
- `6b0260ec Share MoE FFN and Gated DeltaNet graph construction (#1293)`
- `0a71e38b Expose custom-schema results through the C API (#1292)`
- `6f979b93 chatterbox: keep the component and its voices across languages that share a T3 (#1285)`
- `ed0e87a2 campplus: write the graph's constants before every run, not only the first (#1284)`
- `3a08299f cmake: add AUDIOCPP_BUILD_C_API_STATIC option for embedding (libaudiocpp.a) (#1290)`
- `b5286a8d Fix PocketTTS streaming quality and speed up PocketTTS decoders (#1291)`
- `54e0eda6 README: add Woogles-TV to Projects (#1286)`
- `61c7838b Update README for v0.9.5`
- `d38518a0 Add Zonos models to the WebUI and default Audio8 TTS to Realistic (#1283)`
- `5394610a Refactor Zonos3 execution and preserve voice across text chunks (#1282)`
- `51fa5e61 v0.9.5 cleanup: documentation, model manager and test gating (#1281)`
- `69dcc518 New Community Model: ZONOS2 (Zyphra) (#1274)`
- `514d59d0 Add published models to the Web UI (#1279)`
- `78c8d2b7 馃毃 Unify MoE expert offloading through shared framework resources (#1280)`
- `b58b22de New Community Model: ZONOS3-Mini (Zyphra) (#1271)`
- `40c8c868 Add MossFormer2 speech enhancement and super-resolution (#1277)`
- `353bf8c3 feat: add reference-audio voice cloning to KugelAudio (#1275)`
- `ef2a7efd Promote Qwen3 audio tokenizer into framework codecs (#1276)`
- `eb8281c9 pocket_tts: support flow_lm.z_proj when z_dim differs from transformer d_model (#1239)`
- `bf3cabb9 Add Kroko XL offline ASR and improve Zipformer inference (#1273)`
- `eeeca9e8 Add experimental MoE hybrid CPU and GPU expert execution (#1270)`

## RuntimeContract (7)

```text
D	include/engine/framework/core/disk_expert_weight_store.h
M	include/engine/framework/core/module.h
M	include/engine/framework/core/prepared_graph.h
A	include/engine/framework/runtime/moe_options.h
M	include/engine/framework/runtime/session_base.h
A	src/framework/runtime/moe_options.cpp
M	src/framework/runtime/session_base.cpp
```

## NativeBuild (2)

```text
M	.github/workflows/release-hip.yml
M	CMakeLists.txt
```

## ModelCatalog (313)

```text
M	include/engine/community_models/fish_drama3/backbone.h
M	include/engine/community_models/fish_drama3/generator.h
A	include/engine/community_models/fish_drama3/reference_cache.h
M	include/engine/community_models/fish_drama3/session.h
M	include/engine/community_models/granite5asr/assets.h
M	include/engine/community_models/granite5asr/encoder.h
A	include/engine/community_models/granite_speech5/assets.h
A	include/engine/community_models/granite_speech5/decoder.h
A	include/engine/community_models/granite_speech5/encoder.h
A	include/engine/community_models/granite_speech5/frontend.h
A	include/engine/community_models/granite_speech5/projector.h
A	include/engine/community_models/granite_speech5/prompt.h
A	include/engine/community_models/granite_speech5/session.h
M	include/engine/community_models/kroko_asr/assets.h
M	include/engine/community_models/kroko_asr/decoder.h
M	include/engine/community_models/kroko_asr/encoder.h
M	include/engine/community_models/moss_tts_v15/session.h
M	include/engine/community_models/moss_ttsd/session.h
M	include/engine/community_models/moss_voicegen/session.h
M	include/engine/community_models/nemotron_transcribe/decoder.h
M	include/engine/community_models/parakeet_tdt/session.h
A	include/engine/community_models/tiny_voca/assets.h
A	include/engine/community_models/tiny_voca/frontend.h
A	include/engine/community_models/tiny_voca/graph_cache.h
A	include/engine/community_models/tiny_voca/runtime.h
A	include/engine/community_models/tiny_voca/session.h
A	include/engine/community_models/tiny_voca/source_filter.h
M	include/engine/community_models/voxcpm1/audiovae.h
M	include/engine/community_models/voxcpm1/generator.h
A	include/engine/community_models/zonos2/assets.h
A	include/engine/community_models/zonos2/backbone.h
A	include/engine/community_models/zonos2/codec_decoder.h
A	include/engine/community_models/zonos2/generator.h
A	include/engine/community_models/zonos2/loader.h
A	include/engine/community_models/zonos2/npy.h
A	include/engine/community_models/zonos2/prompt.h
A	include/engine/community_models/zonos2/sampler.h
A	include/engine/community_models/zonos2/session.h
A	include/engine/community_models/zonos2/speaker_encoder.h
A	include/engine/community_models/zonos3/assets.h
A	include/engine/community_models/zonos3/backbone.h
A	include/engine/community_models/zonos3/codec_decoder.h
A	include/engine/community_models/zonos3/generator.h
A	include/engine/community_models/zonos3/loader.h
A	include/engine/community_models/zonos3/npy.h
A	include/engine/community_models/zonos3/prompt.h
A	include/engine/community_models/zonos3/reference_encoder.h
A	include/engine/community_models/zonos3/resample.h
A	include/engine/community_models/zonos3/sampler.h
A	include/engine/community_models/zonos3/session.h
A	include/engine/community_models/zonos3/speaker_encoder.h
A	include/engine/community_models/zonos3/ssl_teacher.h
A	include/engine/community_models/zonos3/textnorm.h
A	include/engine/community_models/zonos3/tokenizer.h
M	include/engine/models/ace_step/pre_dit.h
M	include/engine/models/chatterbox/session.h
M	include/engine/models/controlfoley/flow_denoiser.h
M	include/engine/models/cosyvoice3/hift.h
M	include/engine/models/heartmula/session.h
M	include/engine/models/higgs_audio_stt/session.h
M	include/engine/models/higgs_audio_tts/session.h
A	include/engine/models/kugelaudio/reference_encoder.h
A	include/engine/models/m5_tts/ar.h
A	include/engine/models/m5_tts/assets.h
A	include/engine/models/m5_tts/session.h
M	include/engine/models/magpie_tts/runtime.h
M	include/engine/models/magpie_tts/session.h
M	include/engine/models/ming3_audio/backbone.h
M	include/engine/models/mossformer2/assets.h
A	include/engine/models/mossformer2/restoration.h
A	include/engine/models/mossformer2/routing.h
M	include/engine/models/mossformer2/runtime.h
M	include/engine/models/mossformer2/session.h
A	include/engine/models/mossformer2/vocoder.h
M	include/engine/models/nemotron_asr/session.h
M	include/engine/models/omnivoice/generator.h
M	include/engine/models/omnivoice/prompt_builder.h
M	include/engine/models/omnivoice/rknn_generator.h
A	include/engine/models/omnivoice/rknn_zeroop_generator.h
M	include/engine/models/omnivoice/session.h
M	include/engine/models/omnivoice/tokenizer_text.h
M	include/engine/models/pocket_tts/assets.h
D	include/engine/models/pocket_tts/audio_decoder.h
M	include/engine/models/pocket_tts/backend_weights.h
M	include/engine/models/pocket_tts/flow_lm.h
D	include/engine/models/pocket_tts/mimi_decoder.h
M	include/engine/models/pocket_tts/session.h
M	include/engine/models/pocket_tts/text_conditioner.h
M	include/engine/models/pocket_tts_small/assets.h
D	include/engine/models/pocket_tts_small/mimi_decoder.h
M	include/engine/models/pocket_tts_small/session.h
M	include/engine/models/qwen3_tts/prompt_tts_voice_clone.h
M	include/engine/models/qwen3_tts/session.h
D	include/engine/models/qwen3_tts/tokenizer_speech_decoder.h
D	include/engine/models/qwen3_tts/tokenizer_speech_encoder.h
M	include/engine/models/qwen3_tts/types.h
A	include/engine/models/rosvot/runtime.h
A	include/engine/models/rosvot/session.h
M	include/engine/models/sheetsage/types.h
A	include/engine/models/soulx_singer/routing.h
A	include/engine/models/soulx_singer/runtime.h
A	include/engine/models/soulx_singer/score.h
A	include/engine/models/soulx_singer/session.h
M	include/engine/models/vevo2/session.h
M	include/engine/models/vibevoice_asr/connector.h
M	include/engine/models/vibevoice_asr/session.h
M	include/engine/models/vibevoice_asr/speech_tokenizer.h
M	include/engine/models/vibevoice_asr/text_decoder.h
M	include/engine/models/voxcpm2/audiovae.h
M	include/engine/models/voxcpm2/generator.h
M	model_specs/ace_step.json
M	model_specs/audio8_tts.json
M	model_specs/cosyvoice3.json
M	model_specs/fish_drama3.json
M	model_specs/granite5asr.json
A	model_specs/granite_speech5.json
M	model_specs/heartmula.json
M	model_specs/higgs_audio_stt.json
M	model_specs/higgs_audio_tts.json
M	model_specs/kugelaudio.json
A	model_specs/m5_tts.json
M	model_specs/magpie_tts.json
M	model_specs/miocodec.json
M	model_specs/miotts.json
M	model_specs/mossformer2.json
M	model_specs/nemotron_asr.json
M	model_specs/omnivoice.json
M	model_specs/parakeet_tdt.json
A	model_specs/rosvot.json
M	model_specs/sheetsage2.json
A	model_specs/soulx_singer.json
A	model_specs/tiny_voca.json
M	model_specs/vibevoice_asr.json
M	model_specs/voxcpm1.json
M	model_specs/voxcpm2.json
A	model_specs/zonos2.json
A	model_specs/zonos3.json
M	src/community_models/audio8_tts/session.cpp
M	src/community_models/chatterbox_turbo/components/t3_turbo_runtime.h
M	src/community_models/chatterbox_turbo/t3_turbo_weights.cpp
M	src/community_models/echo_tts/dit_blocks.inc
M	src/community_models/echo_tts/session.cpp
M	src/community_models/fish_drama3/backbone.cpp
M	src/community_models/fish_drama3/generator.cpp
M	src/community_models/fish_drama3/loader.cpp
A	src/community_models/fish_drama3/reference_cache.cpp
M	src/community_models/fish_drama3/session.cpp
M	src/community_models/granite5asr/assets.cpp
M	src/community_models/granite5asr/encoder.cpp
M	src/community_models/granite5asr/session.cpp
A	src/community_models/granite_speech5/assets.cpp
A	src/community_models/granite_speech5/decoder.cpp
A	src/community_models/granite_speech5/encoder.cpp
A	src/community_models/granite_speech5/frontend.cpp
A	src/community_models/granite_speech5/projector.cpp
A	src/community_models/granite_speech5/prompt.cpp
A	src/community_models/granite_speech5/session.cpp
M	src/community_models/kroko_asr/assets.cpp
M	src/community_models/kroko_asr/decoder.cpp
M	src/community_models/kroko_asr/encoder.cpp
M	src/community_models/kroko_asr/frontend.cpp
M	src/community_models/kroko_asr/session.cpp
M	src/community_models/kroko_asr/zipformer.cpp
M	src/community_models/nemotron_transcribe/decoder.cpp
M	src/community_models/nemotron_transcribe/session.cpp
M	src/community_models/parakeet_tdt/session.cpp
A	src/community_models/tiny_voca/assets.cpp
A	src/community_models/tiny_voca/dizione/README.md
A	src/community_models/tiny_voca/dizione/align.cpp
A	src/community_models/tiny_voca/dizione/align.h
A	src/community_models/tiny_voca/dizione/convert.cpp
A	src/community_models/tiny_voca/dizione/convert.h
A	src/community_models/tiny_voca/dizione/data.cpp
A	src/community_models/tiny_voca/dizione/dizione.h
A	src/community_models/tiny_voca/dizione/engine.cpp
A	src/community_models/tiny_voca/dizione/g2p.cpp
A	src/community_models/tiny_voca/dizione/g2p.h
A	src/community_models/tiny_voca/dizione/homographs.cpp
A	src/community_models/tiny_voca/dizione/homographs.h
A	src/community_models/tiny_voca/dizione/json.cpp
A	src/community_models/tiny_voca/dizione/json.h
A	src/community_models/tiny_voca/dizione/morphology.cpp
A	src/community_models/tiny_voca/dizione/morphology.h
A	src/community_models/tiny_voca/dizione/normalize.cpp
A	src/community_models/tiny_voca/dizione/normalize.h
A	src/community_models/tiny_voca/dizione/normalize_figures.cpp
A	src/community_models/tiny_voca/dizione/normalize_numbers.cpp
A	src/community_models/tiny_voca/dizione/pyre.cpp
A	src/community_models/tiny_voca/dizione/pyre.h
A	src/community_models/tiny_voca/dizione/sentence.cpp
A	src/community_models/tiny_voca/dizione/sentence.h
A	src/community_models/tiny_voca/dizione/symbols.cpp
A	src/community_models/tiny_voca/dizione/symbols.h
A	src/community_models/tiny_voca/dizione/tables.cpp
A	src/community_models/tiny_voca/dizione/tables.h
A	src/community_models/tiny_voca/dizione/tables_data.inc
A	src/community_models/tiny_voca/dizione/text.cpp
A	src/community_models/tiny_voca/dizione/text.h
A	src/community_models/tiny_voca/dizione/unicode.cpp
A	src/community_models/tiny_voca/dizione/unicode_tables.inc
A	src/community_models/tiny_voca/frontend.cpp
A	src/community_models/tiny_voca/runtime.cpp
A	src/community_models/tiny_voca/session.cpp
A	src/community_models/tiny_voca/source_filter.cpp
M	src/community_models/voxcpm1/session.cpp
M	src/community_models/zipvoice/weights.cpp
M	src/community_models/zipvoice/zipformer.cpp
A	src/community_models/zonos2/assets.cpp
A	src/community_models/zonos2/backbone.cpp
A	src/community_models/zonos2/codec_decoder.cpp
A	src/community_models/zonos2/generator.cpp
A	src/community_models/zonos2/loader.cpp
A	src/community_models/zonos2/npy.cpp
A	src/community_models/zonos2/prompt.cpp
A	src/community_models/zonos2/sampler.cpp
A	src/community_models/zonos2/session.cpp
A	src/community_models/zonos2/speaker_encoder.cpp
A	src/community_models/zonos3/assets.cpp
A	src/community_models/zonos3/backbone.cpp
A	src/community_models/zonos3/codec_decoder.cpp
A	src/community_models/zonos3/generator.cpp
A	src/community_models/zonos3/loader.cpp
A	src/community_models/zonos3/npy.cpp
A	src/community_models/zonos3/prompt.cpp
A	src/community_models/zonos3/reference_encoder.cpp
A	src/community_models/zonos3/resample.cpp
A	src/community_models/zonos3/sampler.cpp
A	src/community_models/zonos3/session.cpp
A	src/community_models/zonos3/speaker_encoder.cpp
A	src/community_models/zonos3/ssl_teacher.cpp
A	src/community_models/zonos3/textnorm.cpp
A	src/community_models/zonos3/tokenizer.cpp
M	src/models/ace_step/loader.cpp
M	src/models/ace_step/pre_dit.cpp
M	src/models/ace_step/request_parser.cpp
M	src/models/chatterbox/components/t3_runtime.h
M	src/models/chatterbox/s3_tokenizer_impl.cpp
M	src/models/chatterbox/session.cpp
M	src/models/chatterbox/t3_weights.cpp
M	src/models/controlfoley/flow_denoiser.cpp
M	src/models/cosyvoice3/hift.cpp
M	src/models/cosyvoice3/session.cpp
M	src/models/dramabox/audio_vae.cpp
M	src/models/dramabox/dit.cpp
M	src/models/dramabox/gemma3_encoder.cpp
M	src/models/dramabox/prompt_connector.cpp
M	src/models/dramabox/vocoder.cpp
M	src/models/higgs_audio_stt/audio_encoder.cpp
M	src/models/higgs_audio_stt/session.cpp
M	src/models/higgs_audio_tts/loader.cpp
A	src/models/kugelaudio/reference_encoder.cpp
M	src/models/kugelaudio/session.cpp
A	src/models/m5_tts/ar.cpp
A	src/models/m5_tts/assets.cpp
A	src/models/m5_tts/session.cpp
M	src/models/magpie_tts/runtime.cpp
M	src/models/meanvc2/session.cpp
M	src/models/meanvc2/speaker_encoder.cpp
M	src/models/ming3_audio/backbone.cpp
M	src/models/ming3_audio/session.cpp
M	src/models/miocodec/loader.cpp
M	src/models/miocodec/session.cpp
M	src/models/miotts/loader.cpp
M	src/models/miotts/session.cpp
M	src/models/mossformer2/assets.cpp
A	src/models/mossformer2/restoration.cpp
A	src/models/mossformer2/routing.cpp
M	src/models/mossformer2/runtime.cpp
M	src/models/mossformer2/session.cpp
A	src/models/mossformer2/vocoder.cpp
M	src/models/mulacover/chord.cpp
M	src/models/nemotron_asr/encoder.cpp
M	src/models/nemotron_asr/loader.cpp
M	src/models/nemotron_asr/session.cpp
M	src/models/omnivoice/generator.cpp
M	src/models/omnivoice/language_map.inc
M	src/models/omnivoice/loader.cpp
M	src/models/omnivoice/prompt_builder.cpp
M	src/models/omnivoice/rknn_generator.cpp
A	src/models/omnivoice/rknn_zeroop/ovz_engine.cpp
A	src/models/omnivoice/rknn_zeroop/ovz_engine.h
A	src/models/omnivoice/rknn_zeroop_generator.cpp
M	src/models/omnivoice/session.cpp
M	src/models/omnivoice/tokenizer_text.cpp
M	src/models/pocket_tts/assets.cpp
D	src/models/pocket_tts/audio_decoder.cpp
M	src/models/pocket_tts/backend_weights.cpp
M	src/models/pocket_tts/flow_lm.cpp
M	src/models/pocket_tts/gradium_phonon.cpp
D	src/models/pocket_tts/mimi_decoder.cpp
M	src/models/pocket_tts/session.cpp
M	src/models/pocket_tts/text_conditioner.cpp
M	src/models/pocket_tts_small/assets.cpp
D	src/models/pocket_tts_small/mimi_decoder.cpp
M	src/models/pocket_tts_small/session.cpp
M	src/models/qwen3_tts/prompt_tts_voice_clone.cpp
M	src/models/qwen3_tts/session.cpp
A	src/models/rosvot/runtime.cpp
A	src/models/rosvot/session.cpp
M	src/models/rvc/synthesizer.cpp
M	src/models/sheetsage/runtime.cpp
A	src/models/soulx_singer/routing.cpp
A	src/models/soulx_singer/runtime.cpp
A	src/models/soulx_singer/score.cpp
A	src/models/soulx_singer/session.cpp
M	src/models/supertonic/runtime.cpp
M	src/models/vevo2/session.cpp
M	src/models/vibevoice_asr/connector.cpp
M	src/models/vibevoice_asr/loader.cpp
M	src/models/vibevoice_asr/session.cpp
M	src/models/vibevoice_asr/speech_tokenizer.cpp
M	src/models/vibevoice_asr/text_decoder.cpp
M	src/models/voxcpm2/loader.cpp
```

## Dependencies (6)

```text
M	external/ggml/include/ggml-backend.h
M	external/ggml/include/ggml.h
M	external/ggml/src/ggml-backend.cpp
M	external/ggml/src/ggml-cuda/ggml-cuda.cu
M	external/ggml/src/ggml-vulkan/ggml-vulkan.cpp
M	external/ggml/src/ggml.c
```

## Tests (95)

```text
A	tests/ace_step/reference_audio.cpp
M	tests/capi/model_test.c
M	tests/capi/parity.py
M	tests/capi/path_test.c
A	tests/codecs/mimi_latent_decoder_lifecycle.cpp
M	tests/community_models/fish_drama3/backbone_allocation_test.cpp
A	tests/community_models/fish_drama3/reference_cache_test.cpp
M	tests/community_models/nemotron_transcribe/decoder_padding_test.cpp
M	tests/community_models/nemotron_transcribe/decoder_safety_test.cpp
A	tests/community_models/tiny_voca/dizione_probe.cpp
A	tests/community_models/tiny_voca/language_test.py
A	tests/community_models/tiny_voca/reference.py
A	tests/community_models/tiny_voca/reference_audio.py
A	tests/community_models/tiny_voca/synth_probe.cpp
A	tests/community_models/tiny_voca/unit_test.cpp
A	tests/community_models/zonos2/make_longform_oracle.py
A	tests/community_models/zonos2/make_oracle.sh
A	tests/community_models/zonos2/oracle_decode.cpp
A	tests/community_models/zonos2/parity.cpp
A	tests/community_models/zonos2/prompt_test.cpp
A	tests/community_models/zonos3/make_oracle.sh
A	tests/community_models/zonos3/parity.cpp
A	tests/community_models/zonos3/text_test.cpp
A	tests/community_models/zonos3/tokenizer_test.cpp
A	tests/community_models/zonos3/tokenizer_vectors.json
A	tests/granite_speech5/README.md
A	tests/granite_speech5/compare_trace.py
A	tests/granite_speech5/decoder_parity.h
A	tests/granite_speech5/dump_c2_fixtures.py
A	tests/granite_speech5/granite_speech5_parity_probe.cpp
A	tests/granite_speech5/reference.py
A	tests/granite_speech5/test_granite_speech5_b1_units.cpp
A	tests/granite_speech5/test_granite_speech5_b2_units.cpp
A	tests/granite_speech5/test_granite_speech5_c1_units.cpp
A	tests/granite_speech5/test_granite_speech5_c2_units.cpp
A	tests/granite_speech5/test_granite_speech5_d_units.cpp
A	tests/granite_speech5/test_granite_speech5_f_units.cpp
A	tests/kugelaudio/add_reference_encoder.py
A	tests/kugelaudio/cloning_session_probe.cpp
M	tests/kugelaudio/convert_gguf.py
A	tests/kugelaudio/reference_encoder.py
A	tests/kugelaudio/reference_encoder_probe.cpp
A	tests/kugelaudio/test_conversion.py
A	tests/kugelaudio/test_reference_audio.cpp
A	tests/m5_tts/LICENSE.model
A	tests/m5_tts/NOTICE
A	tests/m5_tts/components.cpp
A	tests/m5_tts/convert_gguf.py
A	tests/m5_tts/reference.py
M	tests/ming3_audio/thinker_parity.cpp
A	tests/mossformer2/README.md
M	tests/mossformer2/convert_gguf.py
A	tests/mossformer2/reference.py
A	tests/mossformer2/validation.cpp
M	tests/mulacover/components.cpp
A	tests/pocket_tts/mimi_lifecycle.cpp
M	tests/pocket_tts_small/reference.cpp
M	tests/qwen3_tts/test_speech_decoder_streaming.cpp
A	tests/rosvot/LICENSE
A	tests/rosvot/LICENSE.rmvpe
A	tests/rosvot/NOTICE
A	tests/rosvot/README.md
A	tests/rosvot/components.cpp
A	tests/rosvot/convert_gguf.py
A	tests/rosvot/reference.py
A	tests/rosvot/run_validation.py
A	tests/soulx_singer/LICENSE
A	tests/soulx_singer/LICENSE.amphion
A	tests/soulx_singer/LICENSE.rmvpe
A	tests/soulx_singer/LICENSE.whisper
A	tests/soulx_singer/NOTICE
A	tests/soulx_singer/README.md
A	tests/soulx_singer/components.cpp
A	tests/soulx_singer/convert_gguf.py
A	tests/soulx_singer/reference.py
A	tests/soulx_singer/reference_svs.py
A	tests/soulx_singer/run_validation.py
A	tests/unittests/test_allocation_failure.cpp
A	tests/unittests/test_allocation_failure.h
A	tests/unittests/test_chinese_variant.cpp
M	tests/unittests/test_cli_request_options.cpp
A	tests/unittests/test_conv2d_modules.cpp
A	tests/unittests/test_convnext_modules.cpp
D	tests/unittests/test_disk_expert_weight_store.cpp
M	tests/unittests/test_dit_runtime.cpp
M	tests/unittests/test_encoder_modules.cpp
A	tests/unittests/test_gated_delta_net_module.cpp
M	tests/unittests/test_gguf_tensor_source.cpp
A	tests/unittests/test_moe_engine.cpp
A	tests/unittests/test_moe_engine_composition.cpp
R079	tests/unittests/test_moe_module.cpp	tests/unittests/test_moe_engine_math.cpp
A	tests/unittests/test_moe_policy.cpp
A	tests/unittests/test_moe_prepared_graph.cpp
M	tests/unittests/test_prepared_graph.cpp
A	tests/unittests/test_zipformer_modules.cpp
```

## Documentation (41)

```text
M	README.md
M	docs/asr.md
M	docs/audio_tools.md
M	docs/build/rockchip-npu.md
M	docs/c_api.md
M	docs/gguf.md
M	docs/model_licenses.md
M	docs/model_manager.md
M	docs/models/ace_step.md
M	docs/models/audio8_tts.md
M	docs/models/cosyvoice3.md
M	docs/models/fish_drama3.md
M	docs/models/granite5asr.md
A	docs/models/granite_speech5.md
M	docs/models/higgs_audio_stt.md
M	docs/models/higgs_audio_tts.md
M	docs/models/kroko_asr.md
M	docs/models/kugelaudio.md
A	docs/models/m5_tts.md
M	docs/models/magpie_tts.md
M	docs/models/ming3_audio.md
M	docs/models/miotts.md
M	docs/models/mossformer2.md
M	docs/models/mulacover.md
M	docs/models/nemotron_transcribe.md
M	docs/models/omnivoice.md
M	docs/models/parakeet_tdt.md
M	docs/models/qwen3_asr.md
A	docs/models/rosvot.md
A	docs/models/soulx_singer.md
A	docs/models/tiny_voca.md
M	docs/models/vibevoice_asr.md
M	docs/models/voxcpm1.md
A	docs/models/zonos2.md
A	docs/models/zonos3.md
A	docs/reports/granite_speech5_validation.md
A	docs/reports/initial_release_performance.md
A	docs/reports/kugelaudio_reference_cloning.md
A	docs/reports/shared_moe_offload_refactor.md
M	docs/tts.md
M	docs/usage.md
```

## Other (99)

```text
M	CONTRIBUTING.md
M	app/cli/args.cpp
M	app/cli/args.h
M	app/cli/main.cpp
M	app/model_manager/main.cpp
M	include/audiocpp.h
M	include/engine/framework/codecs/heartcodec_runtime.h
M	include/engine/framework/codecs/mimi_codec_runtime.h
M	include/engine/framework/codecs/moss_audio_tokenizer_codec_runtime.h
A	include/engine/framework/codecs/qwen3_audio_tokenizer_runtime.h
M	include/engine/framework/conditioners/open_clip_conditioner_runtime.h
M	include/engine/framework/io/yaml.h
M	include/engine/framework/modules/attention/types.h
M	include/engine/framework/modules/conformer_modules.h
M	include/engine/framework/modules/conv_modules.h
M	include/engine/framework/modules/convnext_modules.h
M	include/engine/framework/modules/feed_forward_modules.h
A	include/engine/framework/modules/gated_delta_net_module.h
D	include/engine/framework/modules/moe_module.h
M	include/engine/framework/modules/pitch_extractors/rmvpe_pitch_extractor.h
M	include/engine/framework/modules/positional_modules.h
D	include/engine/framework/modules/staged_moe_runtime.h
M	include/engine/framework/modules/transformers/causal_decoder.h
M	include/engine/framework/modules/transformers/decoder.h
M	include/engine/framework/modules/transformers/dit.h
M	include/engine/framework/modules/transformers/dit_runtime.h
M	include/engine/framework/modules/vocoders/vocos_vocoder.h
M	include/engine/framework/modules/zipformer_modules.h
A	include/engine/framework/moe_engine/moe_engine.h
A	include/engine/framework/moe_engine/moe_policy.h
A	include/engine/framework/moe_engine/moe_prepared_graph.h
M	include/engine/framework/sampling/device_denoiser_runtime.h
M	include/engine/framework/text/chinese_variant.h
M	scripts/ci/build_hip_release.py
M	src/capi/audiocpp.cpp
M	src/framework/assets/tensor_source.cpp
M	src/framework/codecs/heartcodec_runtime.cpp
A	src/framework/codecs/mimi_latent_decoder_runtime.cpp
M	src/framework/codecs/moss_audio_tokenizer_codec_runtime.cpp
R090	src/models/qwen3_tts/tokenizer_speech_decoder.cpp	src/framework/codecs/qwen3_audio_tokenizer_decoder.cpp
R097	src/models/qwen3_tts/tokenizer_speech_encoder.cpp	src/framework/codecs/qwen3_audio_tokenizer_encoder.cpp
M	src/framework/codecs/s3gen_runtime.cpp
M	src/framework/conditioners/open_clip_conditioner_runtime.cpp
M	src/framework/core/execution_context.cpp
M	src/framework/core/graph_workspace.cpp
M	src/framework/core/prepared_graph.cpp
M	src/framework/decoders/moss_tts_delay/heads.cpp
M	src/framework/io/yaml.cpp
M	src/framework/modules/activation_modules.cpp
M	src/framework/modules/attention/attention_internal.h
M	src/framework/modules/attention/common_relative_attention.cpp
M	src/framework/modules/conformer_modules.cpp
M	src/framework/modules/conv_modules.cpp
M	src/framework/modules/convnext_modules.cpp
M	src/framework/modules/feed_forward_modules.cpp
A	src/framework/modules/gated_delta_net_module.cpp
A	src/framework/modules/pitch_extractors/rmvpe_cuda_runtime.cu
A	src/framework/modules/pitch_extractors/rmvpe_cuda_runtime.h
M	src/framework/modules/pitch_extractors/rmvpe_pitch_extractor.cpp
M	src/framework/modules/positional_modules.cpp
M	src/framework/modules/speaker_encoders/campplus_encoder.cpp
M	src/framework/modules/speech_encoders/wav2vec2_encoder.cpp
M	src/framework/modules/speech_encoders/wavlm_encoder.cpp
D	src/framework/modules/staged_moe_runtime.cpp
M	src/framework/modules/transformers/causal_decoder.cpp
M	src/framework/modules/transformers/causal_decoder_runtime.cpp
M	src/framework/modules/transformers/decoder.cpp
M	src/framework/modules/transformers/dit.cpp
M	src/framework/modules/transformers/dit_runtime.cpp
M	src/framework/modules/vocoders/hifigan_vocoder.cpp
M	src/framework/modules/vocoders/hift_vocoder.cpp
M	src/framework/modules/vocoders/vocos_vocoder.cpp
M	src/framework/modules/zipformer_modules.cpp
A	src/framework/moe_engine/cache_policy.cpp
A	src/framework/moe_engine/expert_execution.cpp
R062	src/framework/modules/moe_module.cpp	src/framework/moe_engine/expert_math.cpp
R062	src/framework/core/disk_expert_weight_store.cpp	src/framework/moe_engine/expert_storage.cpp
A	src/framework/moe_engine/moe_engine.cpp
A	src/framework/moe_engine/moe_engine_internal.h
A	src/framework/moe_engine/moe_policy.cpp
A	src/framework/moe_engine/moe_prepared_graph.cpp
A	src/framework/moe_engine/placement_policy.cpp
M	src/framework/package_manager/xet.cpp
M	src/framework/sampling/device_denoiser_runtime.cpp
M	src/framework/text/chinese_variant.cpp
M	tools/audiocpp_cli/audiocpp_cli_longform_tts_clone_cases.json
M	tools/audiocpp_cli/audiocpp_cli_path_cases.json
A	tools/community_models/convert_kroko_offline_gguf.py
M	tools/community_models/convert_kroko_onnx.py
A	tools/community_models/convert_tiny_voca.py
A	tools/community_models/tiny_voca_tables.py
M	tools/model_manager_v2.py
M	webui/configs/model_params.json
M	webui/configs/models_catalog.json
M	webui/native/dist/index.html
M	webui/native/package-lock.json
M	webui/native/package.json
M	webui/native/src/lib/catalog.ts
M	webui/native/src/routes/+page.svelte
```