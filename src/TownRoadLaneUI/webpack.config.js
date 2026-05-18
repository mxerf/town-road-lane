const path = require("path");
const MOD = require("./mod.json");
const MiniCssExtractPlugin = require("mini-css-extract-plugin");
const { CSSPresencePlugin } = require("./tools/css-presence");
const TerserPlugin = require("terser-webpack-plugin");
const gray = (text) => `\x1b[90m${text}\x1b[0m`;

// Output to a local dist/ folder inside the UI project. The C# csproj's BuildFrontend target
// (mirrors the TTE pattern: TLEFrontend → TrafficToolEssentials build pipeline) picks up
// dist/*.mjs + dist/*.css + mod.json and copies them into its own OutDir, which Mod.targets
// then mirrors into $(LocalModsPath)\TownRoadLane\.
//
// Earlier this script emitted straight into Mods/TownRoadLane/, but the C# Mod.targets does
// <RemoveDir $(DeployDir)> on every build and wiped the .mjs that we'd just placed there —
// the UI went silently missing after every C# rebuild.
const OUTPUT_DIR = "./dist/";
const banner = `\n * Cities: Skylines II UI Module\n * Id: ${MOD.id}\n * Author: ${MOD.author}\n * Version: ${MOD.version}\n`;

module.exports = {
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
