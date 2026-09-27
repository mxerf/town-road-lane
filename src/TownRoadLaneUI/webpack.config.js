const fs = require("fs");
const path = require("path");
const MOD = require("./mod.json");
const MiniCssExtractPlugin = require("mini-css-extract-plugin");
const { CSSPresencePlugin } = require("./tools/css-presence");
const TerserPlugin = require("terser-webpack-plugin");
const gray = (text) => `\x1b[90m${text}\x1b[0m`;

// The csproj's BuildFrontend target copies dist/ into the C# OutDir, and Mod.targets deploys
// it from there (build pipeline modelled on TrafficToolEssentials). Don't emit straight into the mod folder: Mod.targets deletes the deploy
// directory on every C# build, so the bundle would silently disappear.
const OUTPUT_DIR = "./dist/";
// The version comes from ModVersion in PublishConfiguration.xml, the same field the csproj
// uses for the assembly version.
const PUBLISH_CONFIG_PATH = path.resolve(__dirname, "../TownRoadLane/Properties/PublishConfiguration.xml");
const modVersionMatch = /<ModVersion Value="([^"]+)"/.exec(fs.readFileSync(PUBLISH_CONFIG_PATH, "utf8"));
if (!modVersionMatch) throw new Error(`ModVersion not found in ${PUBLISH_CONFIG_PATH}`);
const MOD_VERSION = modVersionMatch[1];
// The banner is the manifest: UIModuleAsset.PostCreate parses it from the .mjs, not from
// mod.json. The Dependencies line is required even when empty. Without it
// m_UIModuleDependencies stays null and PostCreate logs a NullReferenceException on every
// startup.
const banner = `\n * Cities: Skylines II UI Module\n * Id: ${MOD.id}\n * Author: ${MOD.author}\n * Version: ${MOD_VERSION}\n * Dependencies: ${(MOD.dependencies || []).join(", ")}\n`;

module.exports = {
  // Development mode gains nothing: cohtml reports every error at "JS :15:23" either way.
  mode: "production",
  stats: "none",
  entry: { [MOD.id]: "./src/index.tsx" },
  externalsType: "window",
  externals: {
    react: "React",
    "react-dom": "ReactDOM",
    "react-dom/client": "ReactDOM",
    "cs2/modding": "cs2/modding",
    "cs2/api": "cs2/api",
    "cs2/bindings": "cs2/bindings",
    "cs2/l10n": "cs2/l10n",
    "cs2/ui": "cs2/ui",
    "cs2/input": "cs2/input",
    "cs2/utils": "cs2/utils",
    "cohtml/cohtml": "cohtml/cohtml",
  },
  module: {
    rules: [
      { test: /\.tsx?$/, use: "ts-loader", exclude: /node_modules/ },
      {
        test: /\.s?css$/,
        include: path.join(__dirname, "src"),
        use: [
          MiniCssExtractPlugin.loader,
          {
            loader: "css-loader",
            options: {
              url: true,
              importLoaders: 1,
              modules: {
                auto: (resourcePath) => !resourcePath.endsWith("index.scss"),
                exportLocalsConvention: "camelCase",
                localIdentName: "[local]_[hash:base64:3]",
              },
            },
          },
          "sass-loader",
        ],
      },
      { test: /\.(png|jpe?g|gif|svg)$/i, type: "asset/resource", generator: { filename: "images/[name][ext][query]" } },
    ],
  },
  resolve: {
    extensions: [".tsx", ".ts", ".js"],
    modules: ["node_modules", path.join(__dirname, "src")],
    alias: { "mod.json": path.resolve(__dirname, "mod.json") },
  },
  output: {
    path: path.resolve(__dirname, OUTPUT_DIR),
    library: { type: "module" },
    publicPath: `coui://ui-mods/`,
  },
  optimization: {
    minimize: true,
    minimizer: [new TerserPlugin({ extractComments: { banner: () => banner } })],
  },
  experiments: { outputModule: true },
  plugins: [
    new MiniCssExtractPlugin(),
    new CSSPresencePlugin(),
    {
      apply(compiler) {
        let runCount = 0;
        compiler.hooks.done.tap("AfterDonePlugin", (stats) => {
          console.log(stats.toString({ colors: true }));
          console.log(`\n🔨 ${!runCount++ ? "Built" : "Updated"} ${MOD.id}`);
          console.log("   " + gray(OUTPUT_DIR) + "\n");
        });
      },
    },
  ],
};
