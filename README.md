PhiFlow



PhiFlow is a high‑performance .NET library for incremental computation on layered,fixed‑width graphs with real‑time analytics.



It is built for workloads where:

a small subset of inputs changes frequently (events/streaming),

changes propagate through a multi-layer dependency pipeline,

you need fast,exact queries (counts,ranges,sums,Top‑K) on any layer.



PhiFlow avoids full recomputation by tracking the cone of influence of each update (as cache‑friendly intervals) and recomputing only affected nodes. For analytics,PhiFlow can maintain incremental indexes (Fenwick tree,histograms,sums) so repeated queries are cheap and exact.



---



Links



GitHub:https://github.com/likeslines-maker/PhiFlow

Website:https://principium.pro

Telegram:@vipvodu



---



Why PhiFlow



Incremental recompute (Interval Cone-of-Influence)

Instead of recomputing Width \* Layers nodes per update,PhiFlow:

1\) finds the minimal affected set of nodes (per layer) as intervals,

2\) recomputes only those nodes.



This yields major speedups when deltas are small relative to the whole graph.



Exact incremental indexes (no sketches,no approximations)

PhiFlow assumes a bounded discrete value domain (0..DomainSize-1),enabling:

exact count indexes (Fenwick),

exact Top‑K via histograms,

exact sums/averages via running sums,

with 100% correctness.



Two update modes

SetValue updates (recommended for production/business logic)

Mutation updates (deterministic “mutate input” mode for simulations/testing)



---



Quick Start



Install



PhiFlow is distributed under a commercial license (see LICENSE.txt in the package).

```bash

dotnet add package PhiFlow

```



Minimal example

``csharp

using PhiFlow;



var width = 5000;

var layers = 60;

var domain = 1024;



var rt = new PhiFlowRuntime(width,layers,domain);

rt.Reserve(maxDeltaCount:16); // optional but recommended



// Index the last layer (or any layer)

var L = layers - 1;

rt.AttachIndex(L,new FenwickCountIndex(domain));

rt.AttachIndex(L,new SumIndex(width));

rt.AttachIndex(L,new HistogramTopKIndex(domain,width));



// Initialize input layer (layer 0)

var rnd = new Random(1);

var input = new int\[width];

for (int i = 0; i < width; i++) input\[i] = rnd.Next(domain);



rt.SetInput(input);

rt.BuildAll(kWork:50); // one-time full build



// Delta = SET VALUE (production-friendly)

var updates = new InputUpdate\[]

{

&nbsp;new(index:10,value:512),

&nbsp;new(index:123,value:7),

&nbsp;new(index:2048,value:999),

};



rt.ApplyInputUpdates(updates,kWork:50);



// Queries (fast via indexes if attached)

long countGt = rt.CountGreater(L,threshold:500);

long range = rt.RangeCount(L,loInclusive:100,hiExclusive:200);

long sum = rt.Sum(L);

long avg = rt.Avg(L);

long topK = rt.TopKSum(L,k:50);

```



---



Product Tiers \& Pricing



Pricing is subject to contract terms. Contact @vipvodu on Telegram for a quote,invoices,and enterprise agreements.



Model A — Community / Freemium (Developer adoption)

Price: Free

Limits / Features:

Up to 1,000 nodes

Up to 10 layers

Scan-based queries only

No index queries

Basic query types only

No commercial support



Target users: indie devs,research,small prototypes,internal demos.



Model B — Usage-based subscription

Startup — $500/month

Up to 10,000 nodes

All query types

Index queries enabled

Email support

Up to 3 production instances



Business — $2,000/month

Up to 100,000 nodes

Unlimited layers

Priority support

SLA 99.9%

Up to 10 production instances



Enterprise — $7,000+/month

Unlimited nodes

On‑prem option

24/7 support

Team training

Custom contracts / compliance



---



Benchmarks (What we observed)



Your BenchmarkDotNet runs show consistent wins under realistic “update + many queries” workloads,e.g.:



DeltaCount=1,KWork=50:~10.7s → ~0.195s (~55×)

DeltaCount=4,KWork=50:~10.7s → ~0.75s (~14×)

DeltaCount=1,KWork=10:~1.8s → ~0.034s (~53×)



Key pattern:

TopoPhi/PhiFlow incremental recompute provides the big win when deltas are small.

Indexes amplify the win when queries are heavy (threshold/range queries,Top‑K).



Exact numbers depend on graph shape,delta size,query mix,and CPU/memory characteristics.

See PhiFlow.Benchmarks for reproducible BenchmarkDotNet runs.



---



Use Cases (with concrete examples)



PhiFlow is built for real‑time or near‑real‑time systems where small updates cause cascading changes and analytics must be immediate.



1\) FinTech (Priority Segment)



Risk Management (VaR / exposure pipeline)

Layer 0 (inputs): instrument price changes,rates,vol surfaces

Layer 1: instrument‑level risk metrics (delta/gamma/vega proxies)

Layer 2: sector aggregation

Layer 3: portfolio aggregation

Layer 4: enterprise risk limits and alerts



PhiFlow advantage: a small set of market ticks updates only affected branches; queries like “exposure > threshold” are instant via Fenwick.



Algorithmic Trading Signals

tick → bars → features → signal → strategy decision

Use Top‑K and threshold queries to identify hottest instruments / anomalies.



Anti‑Fraud / Transaction Scoring

transaction → user → account → cluster → risk flag

Low-latency recompute of cascading scores and instant threshold/range checks.



---



2\) Gaming / Simulation



4X / economy simulation

Layer 0: local changes (tax rate in a province,resource availability)

Layer 1: province income

Layer 2: happiness / unrest

Layer 3: productivity

Layer 4: army power / upkeep



PhiFlow advantage: “what happens if I change X?” becomes an immediate recalculation with fast aggregate queries for UI.



MMO balancing \& live ops

action → class stats → global balance metrics → tuning suggestions

Top‑K queries can surface the most unbalanced regions/classes instantly.



---



3\) IIoT / Telemetry



Predictive maintenance

sensor → machine → production line → plant → region

When a single sensor spikes,only relevant aggregates update.



Energy / smart grid

consumer → transformer → district → regional load

RangeCount/CountGreater queries power real-time alarms and dashboards.



Logistics

item → warehouse → hub → route → global chain

Fast recompute of derived KPIs when inventory or route conditions change.



---



4\) AdTech / Marketing



Real‑time bidding / campaign pacing

impression → user → segment → campaign → budget

Small deltas (a user action) propagate and you query “segments exceeding threshold” instantly.



Attribution pipeline

click → visit → conversion → funnel → ROI

Incremental updates allow dashboards to stay “live”.



---



5\) Observability / DevOps



SLA monitoring

request → service instance → service → dependency graph → overall SLA

One latency spike updates the impacted aggregates immediately.



Infrastructure metrics

process → pod → node → cluster → region

CountGreater is used heavily for alert thresholds; PhiFlow’s indexing makes it fast.



---



6\) Science \& Engineering (Layered compute pipelines)



Grid / stencil-like pipelines

boundary conditions → local cell updates → higher-level aggregates

PhiFlow’s interval propagation is naturally aligned with “localized influence”.



Epidemic / ecology models

entity → neighborhood → region → country → global metrics

Incremental updates allow live what‑if analysis and immediate aggregation queries.



---



How PhiFlow Compares (Positioning)



PhiFlow fits between:

stream processors (Flink/Kafka Streams),

OLAP databases (ClickHouse/Druid),

custom in-memory triggers/caches,



but focuses on hierarchical / layered incremental recompute + exact query indexes.



Flink/Kafka Streams: powerful but heavyweight for tight in-memory hierarchical recompute.

ClickHouse/Druid: great for OLAP; real-time per-event cascading updates are not their core.

Custom code: expensive to build and hard to keep correct; PhiFlow provides a tested kernel + indexes.



---



Recommended Integration Pattern



1\) Model your pipeline as layers:

&nbsp;- Width = number of entities per layer

&nbsp;- Layers = number of aggregation stages

2\) Feed updates into layer 0 using ApplyInputUpdates(...).

3\) Attach indexes to layers that you query frequently.

4\) Route queries through PhiFlow (index-backed when available).



---



License



PhiFlow is distributed under a commercial license.

See LICENSE.txt included with the package.



---



Contact



Telegram:@vipvodu

GitHub:https://github.com/likeslines-maker/PhiFlow

Website:https://principium.pro





