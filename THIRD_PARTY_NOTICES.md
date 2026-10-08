# 第三方组件与模型说明

本目录随 0.1.0 预览版和源码发布。项目自有源码的使用许可证尚未指定；下表的许可只适用于各自的第三方组件与模型。

| 内容 | 固定版本 / 来源 | 许可与随附材料 |
|---|---|---|
| RapidOcrNet | 4.2.0；NuGet 元数据对应源码提交 `708cae2fcb88720e1d891a81b5ee3e8b2bcc139e`；作者 BobLd、RapidOCR | Apache-2.0；完整文本位于 licenses/Apache-2.0.txt；[源码](https://github.com/BobLd/RapidOcrNet) |
| PP-OCRv5 检测 / 英日 / 韩识别与方向模型 | RapidAI/RapidOCR ModelScope 仓库的 `v3.9.2/onnx/PP-OCRv5` 文件；具体源地址和 SHA256 见 models/screen-ocr/manifest.json | Apache-2.0；保留 RapidOCR 与 PaddleOCR 许可；[模型清单](https://raw.githubusercontent.com/RapidAI/RapidOCR/main/python/rapidocr/default_models.yaml)、[RapidOCR](https://github.com/RapidAI/RapidOCR)、[PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) |
| ONNX Runtime / Managed | 1.29.0 | MIT；包内 LICENSE 与 ThirdPartyNotices 已复制到 licenses；[源码](https://github.com/microsoft/onnxruntime) |
| SkiaSharp / Windows 原生库 | 3.119.1 | MIT 与依赖声明；包内许可已保留；[源码](https://github.com/mono/SkiaSharp) |
| Clipper2 | 2.0.0 | Boost Software License 1.0；包内 License.txt；[源码](https://github.com/AngusJohnson/Clipper2) |
| System.Numerics.Tensors | 9.0.0 | MIT 与包内第三方声明；[源码](https://github.com/dotnet/runtime) |
| .NET / Windows Desktop 运行环境 | .NET 10；构建 SDK 10.0.401 | Microsoft 随 SDK 提供的 LICENSE 与 ThirdPartyNotices 已复制到 licenses；[源码](https://github.com/dotnet/dotnet) |
| Inno Setup 安装程序与卸载程序 | 7.1.0；官方 `jrsoftware/issrc` 的 `is-7_1_0` 发布，编译工具只放入本项目 | 原始安装引擎未修改；许可文本位于 licenses/InnoSetup-LICENSE.txt；[源码与下载](https://github.com/jrsoftware/issrc/releases/tag/is-7_1_0) |

模型未经修改；配套字典在运行时从已固定模型的 `character` 元数据中提取。项目使用自己的英日韩模型组合，不采用 RapidOcrNet 包默认复制的拉丁语模型。

NuGet 同时还原了 macOS / Linux 的 SkiaSharp 原生包，这是上游依赖关系；本候选程序仅面向 Windows x64。它们的许可也保留在 licenses 目录，不能据此声称其他平台可运行。

模型图像和测试文字是为本项目在本机生成的合成样本；软件没有附带游戏截图或用户屏幕内容。
