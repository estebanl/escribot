import type { ChartSpec, TaskType, PromptCategory } from "@/lib/types";

export interface SeedPromptEntry {
  id: string;
  taskType: TaskType;
  category: PromptCategory;
  topic: string;
  title: string;
  promptText: string;
  chartSpec?: ChartSpec;
  imageDataUrl?: string | null;
}

export const CURATED_IELTS_PROMPTS: SeedPromptEntry[] = [
  /* ==================== CAMBRIDGE IELTS 21 (ACADEMIC TESTS 1–4) ==================== */
  {
    id: "ielts21-test1-task1-us-jobs",
    taskType: "task1",
    category: "line",
    topic: "Labour & Employment",
    title: "Cambridge 21 Test 1: Number of Jobs in Four US Economic Sectors (1960–2020)",
    promptText:
      "The graph below gives information about the number of jobs in four sectors of the economy in the US between 1960 and 2020.\n\nSummarise the information by selecting and reporting the main features, and make comparisons where relevant.\n\nWrite at least 150 words.",
    imageDataUrl: "/ielts21/ielts21-t1-test1.webp",
    chartSpec: {
      kind: "line",
      title: "Number of jobs in four sectors of the economy in the US, 1960–2020",
      subtitle: "Jobs in millions",
      xAxisKey: "year",
      yAxisUnit: "M",
      series: [
        { key: "manufacturing", label: "Manufacturing", color: "#16656e" },
        { key: "retail", label: "Retail", color: "#c55e1b" },
        { key: "healthcare", label: "Healthcare", color: "#2b7a52" },
        { key: "agriculture", label: "Agriculture", color: "#475569" },
      ],
      data: [
        { year: "1960", manufacturing: 15, retail: 6, healthcare: 2, agriculture: 6 },
        { year: "1980", manufacturing: 20, retail: 10, healthcare: 5, agriculture: 2.7 },
        { year: "2000", manufacturing: 17, retail: 15, healthcare: 11, agriculture: 2.8 },
        { year: "2020", manufacturing: 13, retail: 16, healthcare: 16.5, agriculture: 2 },
      ],
      sourceNote: "Source: Cambridge IELTS 21 Academic, Test 1, Writing Task 1",
    },
  },
  {
    id: "ielts21-test1-task2-tall-apartments",
    taskType: "task2",
    category: "opinion",
    topic: "Urban Housing & Architecture",
    title: "Cambridge 21 Test 1: High-Rise Apartment Blocks for City Housing",
    promptText:
      "The best way to provide enough homes in large cities is to build tall apartment blocks.\n\nTo what extent do you agree or disagree with this statement?\n\nGive reasons for your answer and include any relevant examples from your own knowledge or experience.\n\nWrite at least 250 words.",
  },
  {
    id: "ielts21-test2-task1-cafe-redesign",
    taskType: "task1",
    category: "map",
    topic: "Campus Planning & Architecture",
    title: "Cambridge 21 Test 2: College Café Floor Plan (Before Redesign vs. Today)",
    promptText:
      "The plans below show a college café before it was redesigned and how it looks now.\n\nSummarise the information by selecting and reporting the main features, and make comparisons where relevant.\n\nWrite at least 150 words.",
    imageDataUrl: "/ielts21/ielts21-t1-test2.webp",
    chartSpec: {
      kind: "map",
      title: "College Café Layout: Before Redesign vs. Café Today",
      subtitle: "Reconfiguration of food service counters, seating arrangements, and outdoor facilities",
      periodA: "Before Redesign",
      periodB: "Café Today",
      waterway: "none",
      mainRoad: "none",
      featuresA: [
        { zone: "NW", label: "Kitchen", category: "civic" },
        { zone: "N", label: "Serving Area (All Food & Drink)", category: "commercial" },
        { zone: "NE", label: "Staff Dining Rooms (Enclosed)", category: "civic" },
        { zone: "W", label: "Main Entrance", category: "transport" },
        { zone: "C", label: "Tables and Chairs", category: "leisure" },
        { zone: "E", label: "Tables and Chairs & Waste Bin", category: "industrial" },
        { zone: "SW", label: "Side Entrance & Seating", category: "transport" },
        { zone: "S", label: "Indoor Dining Area", category: "leisure" },
        { zone: "SE", label: "Toilets", category: "civic" },
      ],
      featuresB: [
        { zone: "NW", label: "Kitchen & Hot Meals Counter", category: "commercial", changed: true },
        { zone: "N", label: "Take Away Food Counter", category: "commercial", changed: true },
        { zone: "NE", label: "Coffee Bar", category: "commercial", changed: true },
        { zone: "W", label: "Salad Bar (West Wall)", category: "commercial", changed: true },
        { zone: "C", label: "Tables and Chairs (Rearranged)", category: "leisure", changed: true },
        { zone: "E", label: "Open Circulation Corridor", category: "transport", changed: true },
        { zone: "SW", label: "Barbecue Station", category: "commercial", changed: true },
        { zone: "S", label: "Outdoor Seating (Picnic Benches)", category: "leisure", changed: true },
        { zone: "SE", label: "Toilets & Recycling Bins", category: "civic", changed: true },
      ],
    },
  },
  {
    id: "ielts21-test2-task2-theatres-cinemas",
    taskType: "task2",
    category: "discussion",
    topic: "Arts, Culture & Digital Media",
    title: "Cambridge 21 Test 2: Theatres and Cinemas in the Digital Age",
    promptText:
      "Some people say that in the digital age, theatres and cinemas are no longer important as people can watch all the entertainment they want online. Others argue that theatres and cinemas are still important both economically and culturally.\n\nDiscuss both these views and give your own opinion.\n\nGive reasons for your answer and include any relevant examples from your own knowledge or experience.\n\nWrite at least 250 words.",
  },
  {
    id: "ielts21-test3-task1-rain-shadow-desert",
    taskType: "task1",
    category: "process",
    topic: "Geography & Climatology",
    title: "Cambridge 21 Test 3: Formation of Rain-Shadow Deserts",
    promptText:
      "The diagram below shows how one type of desert, known as a rain-shadow desert, is formed.\n\nSummarise the information by selecting and reporting the main features, and make comparisons where relevant.\n\nWrite at least 150 words.",
    imageDataUrl: "/ielts21/ielts21-t1-test3.webp",
    chartSpec: {
      kind: "process",
      title: "The Formation of Rain-Shadow Deserts",
      subtitle: "Seven-stage orographic lift and leeward desertification across thousands of kilometres",
      isCyclic: false,
      steps: [
        {
          stepNumber: 1,
          stageName: "Winds Approach Coast",
          detail: "Prevailing maritime winds gather abundant moisture while traversing open ocean waters toward the coastline.",
          badge: "1 · Maritime Airflow",
        },
        {
          stepNumber: 2,
          stageName: "Winds Pushed Upwards",
          detail: "Upon making landfall, dense air currents encounter the mountain barrier and are forced up coastal slopes.",
          badge: "2 · Orographic Lift",
        },
        {
          stepNumber: 3,
          stageName: "Moist Air Rises and Cools",
          detail: "Ascending air masses on the windward side expand under decreasing atmospheric pressure and cool rapidly.",
          badge: "3 · Windward Ascent",
        },
        {
          stepNumber: 4,
          stageName: "Clouds Form",
          detail: "Water vapour condenses as air reaches dew point, generating dense cloud formations above mountain slopes.",
          badge: "4 · Condensation",
        },
        {
          stepNumber: 5,
          stageName: "Rain Falls",
          detail: "Copious precipitation falls on the windward flank of the range, shedding virtually all atmospheric moisture.",
          badge: "5 · Windward Precipitation",
        },
        {
          stepNumber: 6,
          stageName: "Dry Air Continues Over Mountain",
          detail: "Depleted of water vapour, dry air crests the summit and descends down the sheltered leeward slope, warming by compression.",
          badge: "6 · Leeward Descent",
        },
        {
          stepNumber: 7,
          stageName: "Dry Winds Reach Inland Areas",
          detail: "Warm, moisture-deficient winds sweep across interior plains, forming an extensive rain-shadow desert extending thousands of kilometres.",
          badge: "7 · Arid Desertification",
        },
      ],
    },
  },
  {
    id: "ielts21-test3-task2-undergraduate-abroad",
    taskType: "task2",
    category: "advantages",
    topic: "Higher Education & Global Careers",
    title: "Cambridge 21 Test 3: Compulsory Study Abroad or Work Placements for Undergraduates",
    promptText:
      "All university undergraduate courses should include a period of time spent studying abroad or doing a work placement.\n\nDo you think the advantages of this would outweigh the disadvantages?\n\nGive reasons for your answer and include any relevant examples from your own knowledge or experience.\n\nWrite at least 250 words.",
  },
  {
    id: "ielts21-test4-task1-library-survey",
    taskType: "task1",
    category: "mixed",
    topic: "University Infrastructure & Services",
    title: "Cambridge 21 Test 4: University Library User Demographics & Satisfaction Survey",
    promptText:
      "The chart and table below show the results of a survey of library users at a university.\n\nSummarise the information by selecting and reporting the main features, and make comparisons where relevant.\n\nWrite at least 150 words.",
    imageDataUrl: "/ielts21/ielts21-t1-test4.webp",
    chartSpec: {
      kind: "composite",
      title: "University Library Survey: User Breakdown and Service Satisfaction Ratings",
      subtitle: "Proportion of student and staff users alongside satisfaction ratings across five key library services (%)",
      visuals: [
        {
          kind: "pie",
          title: "Categories of Library Users",
          subtitle: "Survey sample distribution across 5 academic cohorts (%)",
          unit: "%",
          charts: [
            {
              label: "Library User Demographics",
              slices: [
                { name: "Full-time undergraduate", value: 44 },
                { name: "Full-time postgraduate", value: 25 },
                { name: "Part-time postgraduate", value: 16 },
                { name: "Distance learning (all courses)", value: 8 },
                { name: "Academic staff", value: 7 },
              ],
            },
          ],
        },
        {
          kind: "table",
          title: "Library User Satisfaction (%)",
          unitNote: "Percentage breakdown across three satisfaction ratings",
          columns: ["Library Service / Facility", "Very satisfied", "Fairly satisfied", "Not satisfied"],
          rows: [
            ["Library opening hours", "65%", "35%", "0%"],
            ["Helpfulness of staff", "95%", "5%", "0%"],
            ["Availability of books", "50%", "40%", "10%"],
            ["Availability of journals", "45%", "35%", "20%"],
            ["Reliability of wi-fi", "48%", "33%", "19%"],
          ],
        },
      ],
      sourceNote: "Source: Cambridge IELTS 21 Academic, Test 4, Writing Task 1",
    },
  },
  {
    id: "ielts21-test4-task2-primary-school-play",
    taskType: "task2",
    category: "two_part",
    topic: "Early Childhood Education & Pedagogy",
    title: "Cambridge 21 Test 4: Formal Learning vs. Play in Primary Classrooms",
    promptText:
      "Some people argue that primary schools focus too much on formal learning.\n\nTo what extent do you agree with this opinion?\n\nHow important do you think it is for children to play as well as learn in the primary school classroom?\n\nGive reasons for your answer and include any relevant examples from your own knowledge or experience.\n\nWrite at least 250 words.",
  },
  /* ==================== ACADEMIC TASK 1 (ALL 7 VISUAL TYPES) ==================== */
  {
    id: "t1-line-uk-electricity",
    taskType: "task1",
    category: "line",
    topic: "Energy & Environment",
    title: "Electricity Generation by Source in the UK (1990–2025)",
    promptText:
      "The graph below shows the percentage of total electricity generated from four different energy sources in the United Kingdom between 1990 and 2025.\n\nSummarise the information by selecting and reporting the main features, and make comparisons where relevant.\n\nWrite at least 150 words.",
    chartSpec: {
      kind: "line",
      title: "Share of UK Electricity Generation by Fuel Source (1990–2025)",
      subtitle: "Percentage of total national terawatt-hours (%)",
      xAxisKey: "year",
      yAxisUnit: "%",
      series: [
        { key: "coal", label: "Coal", color: "#475569" },
        { key: "gas", label: "Natural Gas", color: "#c25e1a" },
        { key: "nuclear", label: "Nuclear", color: "#1d6f78" },
        { key: "renewables", label: "Renewables (Wind, Solar & Hydro)", color: "#228b5b" },
      ],
      data: [
        { year: "1990", coal: 65, gas: 5, nuclear: 21, renewables: 2 },
        { year: "1995", coal: 47, gas: 19, nuclear: 26, renewables: 2 },
        { year: "2000", coal: 32, gas: 39, nuclear: 22, renewables: 3 },
        { year: "2005", coal: 34, gas: 39, nuclear: 20, renewables: 4 },
        { year: "2010", coal: 28, gas: 46, nuclear: 16, renewables: 7 },
        { year: "2015", coal: 22, gas: 30, nuclear: 21, renewables: 25 },
        { year: "2020", coal: 2, gas: 36, nuclear: 16, renewables: 43 },
        { year: "2025", coal: 0, gas: 30, nuclear: 15, renewables: 51 },
      ],
      sourceNote: "Source: National Energy Statistical Digest",
    },
  },
  {
    id: "t1-bar-research-funding",
    taskType: "task1",
    category: "bar",
    topic: "Higher Education & Science",
    title: "Postgraduate Research Funding by Discipline in Three Countries",
    promptText:
      "The chart below compares the proportion of public postgraduate research funding allocated to five academic fields in Germany, South Korea, and Canada in 2024.\n\nSummarise the information by selecting and reporting the main features, and make comparisons where relevant.\n\nWrite at least 150 words.",
    chartSpec: {
      kind: "bar",
      title: "Allocation of Public Postgraduate Research Grants by Field (2024)",
      subtitle: "Percentage of total national postgraduate grant budget (%)",
      xAxisKey: "discipline",
      yAxisUnit: "%",
      series: [
        { key: "germany", label: "Germany", color: "#16656e" },
        { key: "southKorea", label: "South Korea", color: "#c55e1b" },
        { key: "canada", label: "Canada", color: "#2b7a52" },
      ],
      data: [
        { discipline: "Engineering & AI", germany: 34, southKorea: 42, canada: 26 },
        { discipline: "Biomedical Sciences", germany: 25, southKorea: 22, canada: 31 },
        { discipline: "Clean Energy & Climate", germany: 21, southKorea: 18, canada: 22 },
        { discipline: "Social Sciences", germany: 12, southKorea: 11, canada: 13 },
        { discipline: "Arts & Humanities", germany: 8, southKorea: 7, canada: 8 },
      ],
      sourceNote: "Source: OECD Higher Education Research Indicators, 2024",
    },
  },
  {
    id: "t1-pie-water-melbourne",
    taskType: "task1",
    category: "pie",
    topic: "Urban Infrastructure",
    title: "Residential Water Use in Melbourne (1995 vs. 2025)",
    promptText:
      "The two pie charts below show the proportion of residential water consumed across five domestic categories in Melbourne in 1995 and 2025, alongside average daily per-capita usage.\n\nSummarise the information by selecting and reporting the main features, and make comparisons where relevant.\n\nWrite at least 150 words.",
    chartSpec: {
      kind: "pie",
      title: "Domestic Water Consumption Breakdown in Melbourne",
      subtitle: "Average daily usage fell from 290 litres/person (1995) to 165 litres/person (2025)",
      unit: "%",
      charts: [
        {
          label: "1995 (290 L / person / day)",
          slices: [
            { name: "Garden & Lawn Irrigation", value: 38 },
            { name: "Showers & Baths", value: 24 },
            { name: "Laundry", value: 18 },
            { name: "Toilets", value: 13 },
            { name: "Kitchen & Drinking", value: 7 },
          ],
        },
        {
          label: "2025 (165 L / person / day)",
          slices: [
            { name: "Garden & Lawn Irrigation", value: 16 },
            { name: "Showers & Baths", value: 33 },
            { name: "Laundry", value: 21 },
            { name: "Toilets", value: 17 },
            { name: "Kitchen & Drinking", value: 13 },
          ],
        },
      ],
      sourceNote: "Source: Metropolitan Water Authority",
    },
  },
  {
    id: "t1-table-highspeed-rail",
    taskType: "task1",
    category: "table",
    topic: "Transport & Economy",
    title: "High-Speed Rail Networks in Five Countries (2025)",
    promptText:
      "The table below gives data on the high-speed rail networks of five countries in 2025, including operational track length, annual passenger journeys, network utilisation per kilometre, and average ticket price.\n\nSummarise the information by selecting and reporting the main features, and make comparisons where relevant.\n\nWrite at least 150 words.",
    chartSpec: {
      kind: "table",
      title: "High-Speed Rail Network Metrics Across Five Countries (2025)",
      unitNote: "Fares standardized to USD per 100 kilometres travelled",
      columns: [
        "Country",
        "Operational Track (km)",
        "Annual Passengers (millions)",
        "Avg. Speed (km/h)",
        "Avg. Fare (USD / 100 km)",
      ],
      rows: [
        ["China", "45,000", "2,940", 305, "$7.80"],
        ["Spain", "3,960", "39", 285, "$14.20"],
        ["Japan", "3,140", "368", 275, "$22.50"],
        ["France", "2,800", "126", 290, "$17.90"],
        ["South Korea", "890", "104", 260, "$13.40"],
      ],
      sourceNote: "Source: International Union of Railways (UIC) Annual Report",
    },
  },
  {
    id: "t1-mixed-copenhagen-cycling",
    taskType: "task1",
    category: "mixed",
    topic: "Urban Mobility",
    title: "Municipal Cycling Investment & Commuter Modal Share in Copenhagen",
    promptText:
      "The chart below shows annual municipal capital expenditure on cycling infrastructure (in millions of euros) and the percentage of commuter trips made by bicycle in Copenhagen between 2000 and 2024.\n\nSummarise the information by selecting and reporting the main features, and make comparisons where relevant.\n\nWrite at least 150 words.",
    chartSpec: {
      kind: "mixed",
      title: "Cycling Infrastructure Spending vs. Commuter Bicycle Share (2000–2024)",
      subtitle: "Bars: Capital investment (€M, left axis) · Line: Bicycle commuter share (%, right axis)",
      xAxisKey: "year",
      yAxisUnit: "€M",
      yAxisRightUnit: "%",
      series: [
        {
          key: "investmentEurM",
          label: "Capital Investment (€M)",
          type: "bar",
          yAxisId: "left",
          color: "#16656e",
        },
        {
          key: "commuterSharePct",
          label: "Bicycle Commuter Share (%)",
          type: "line",
          yAxisId: "right",
          color: "#c55e1b",
        },
      ],
      data: [
        { year: "2000", investmentEurM: 9, commuterSharePct: 32 },
        { year: "2004", investmentEurM: 14, commuterSharePct: 34 },
        { year: "2008", investmentEurM: 22, commuterSharePct: 37 },
        { year: "2012", investmentEurM: 38, commuterSharePct: 41 },
        { year: "2016", investmentEurM: 46, commuterSharePct: 45 },
        { year: "2020", investmentEurM: 35, commuterSharePct: 47 },
        { year: "2024", investmentEurM: 41, commuterSharePct: 49 },
      ],
      sourceNote: "Source: City of Copenhagen Bicycle Account",
    },
  },
  {
    id: "t1-process-lithium-recycling",
    taskType: "task1",
    category: "process",
    topic: "Industrial Engineering",
    title: "Closed-Loop Hydrometallurgical Recycling of Lithium-Ion Batteries",
    promptText:
      "The diagram below illustrates the industrial process by which spent electric-vehicle lithium-ion batteries are recycled to manufacture new battery cells.\n\nSummarise the information by selecting and reporting the main features, and make comparisons where relevant.\n\nWrite at least 150 words.",
    chartSpec: {
      kind: "process",
      title: "Closed-Loop Hydrometallurgical Battery Recycling Process",
      subtitle: "Seven-stage industrial recovery cycle from spent EV packs to cathode synthesis",
      isCyclic: true,
      steps: [
        {
          stepNumber: 1,
          stageName: "Collection & Deep Discharge",
          detail: "End-of-life EV packs are tested and electrically discharged in brine tanks to prevent thermal hazards.",
          badge: "Safety Prep",
        },
        {
          stepNumber: 2,
          stageName: "Automated Disassembly",
          detail: "Robotic units remove aluminium casings, wiring harnesses, and plastic modules for separate recycling.",
          badge: "Mechanical",
        },
        {
          stepNumber: 3,
          stageName: "Inert-Gas Shredding",
          detail: "Cells are crushed under nitrogen atmosphere (oxygen-free) into a fine granular mixture.",
          badge: "N₂ Chamber",
        },
        {
          stepNumber: 4,
          stageName: "Sieving & Magnetic Separation",
          detail: "Copper and steel foils are filtered out, isolating 'black mass' powder rich in critical metals.",
          badge: "Black Mass",
        },
        {
          stepNumber: 5,
          stageName: "Acid Leaching & Filtration",
          detail: "Black mass is dissolved in warm sulphuric acid solution (60°C) to extract metal ions into liquid phase.",
          badge: "Chemical 60°C",
        },
        {
          stepNumber: 6,
          stageName: "Solvent Precipitation",
          detail: "Battery-grade lithium, nickel, and cobalt salts (99.5% purity) are selectively crystallized.",
          badge: "99.5% Purity",
        },
        {
          stepNumber: 7,
          stageName: "Cathode Re-Manufacturing",
          detail: "Purified salts are synthesized into active cathode material for new EV battery cells.",
          badge: "Closed Loop",
        },
      ],
    },
  },
  {
    id: "t1-map-port-haverford",
    taskType: "task1",
    category: "map",
    topic: "Urban Planning",
    title: "Redevelopment of Port Haverford Waterfront (1995 vs. Present)",
    promptText:
      "The two maps below show the coastal district of Port Haverford in 1995 and as it appears at the present day following urban regeneration.\n\nSummarise the information by selecting and reporting the main features, and make comparisons where relevant.\n\nWrite at least 150 words.",
    chartSpec: {
      kind: "map",
      title: "Port Haverford Harbour District: 1995 vs. Present Day",
      subtitle: "Southern coastline faces the harbour; Main Coastal Road runs East–West across the centre",
      periodA: "1995",
      periodB: "Present Day",
      waterway: "coastal_south",
      mainRoad: "horizontal",
      featuresA: [
        { zone: "NW", label: "Pine Woodland", category: "nature" },
        { zone: "N", label: "Low-Density Cottages", category: "residential" },
        { zone: "NE", label: "Agricultural Farmland", category: "nature" },
        { zone: "W", label: "Bus Depot & Car Park", category: "transport" },
        { zone: "C", label: "Town Market Square", category: "civic" },
        { zone: "E", label: "Textile Warehouses", category: "industrial" },
        { zone: "SW", label: "Fishing Boat Pier", category: "industrial" },
        { zone: "S", label: "Cargo Container Docks", category: "industrial" },
        { zone: "SE", label: "Coal Storage Yard", category: "industrial" },
      ],
      featuresB: [
        { zone: "NW", label: "Public Eco-Park (Reduced)", category: "leisure", changed: true },
        { zone: "N", label: "Mid-Rise Apartments", category: "residential", changed: true },
        { zone: "NE", label: "Marine Tech University Campus", category: "civic", changed: true },
        { zone: "W", label: "Light-Rail Terminus", category: "transport", changed: true },
        { zone: "C", label: "Pedestrianised Market Plaza", category: "commercial", changed: true },
        { zone: "E", label: "Co-Working & Art Lofts (Converted)", category: "commercial", changed: true },
        { zone: "SW", label: "Yacht Marina & Cafés", category: "leisure", changed: true },
        { zone: "S", label: "Waterfront Boardwalk", category: "leisure", changed: true },
        { zone: "SE", label: "Passenger Ferry Terminal", category: "transport", changed: true },
      ],
    },
  },

  /* ==================== ACADEMIC TASK 2 (ALL 5 ESSAY ARCHETYPES) ==================== */
  {
    id: "t2-opinion-pure-research",
    taskType: "task2",
    category: "opinion",
    topic: "Science & Public Policy",
    title: "Public Funding for Theoretical vs. Applied University Research",
    promptText:
      "Some people argue that public university funding should be restricted to scientific research with clear, immediate economic or social applications, rather than theoretical fields such as pure mathematics or astrophysics.\n\nTo what extent do you agree or disagree?\n\nGive reasons for your answer and include any relevant examples from your own knowledge or experience. Write at least 250 words.",
  },
  {
    id: "t2-discussion-carfree-cities",
    taskType: "task2",
    category: "discussion",
    topic: "Urbanisation & Transport",
    title: "Banning Private Cars from Metropolitan Centres",
    promptText:
      "Some urban planners believe that banning private motor vehicles from city centres is the most effective way to reduce pollution and congestion, while others contend that such policies unfairly penalise outer-suburb residents and small businesses.\n\nDiscuss both these views and give your own opinion.\n\nGive reasons for your answer and include any relevant examples from your own knowledge or experience. Write at least 250 words.",
  },
  {
    id: "t2-advantages-algorithmic-work",
    taskType: "task2",
    category: "advantages",
    topic: "Work & Technology",
    title: "Algorithmic Screening and AI Productivity Tracking at Work",
    promptText:
      "An increasing number of organisations now rely on automated algorithms to screen job applicants and monitor employee productivity.\n\nDo the advantages of this development outweigh the disadvantages?\n\nGive reasons for your answer and include any relevant examples from your own knowledge or experience. Write at least 250 words.",
  },
  {
    id: "t2-probsol-deep-reading",
    taskType: "task2",
    category: "problem_solution",
    topic: "Education & Digital Media",
    title: "Declining Long-Form Reading Stamina Among University Students",
    promptText:
      "Educators in many countries report that incoming university students increasingly struggle to engage with complex, book-length academic texts.\n\nWhat are the primary causes of this trend, and what measures could schools and universities take to address it?\n\nGive reasons for your answer and include any relevant examples from your own knowledge or experience. Write at least 250 words.",
  },
  {
    id: "t2-twopart-delayed-parenthood",
    taskType: "task2",
    category: "two_part",
    topic: "Demographics & Society",
    title: "Delayed Parenthood in Industrialised Nations",
    promptText:
      "In many industrialised societies, people are choosing to have children significantly later in life than previous generations did.\n\nWhy is this change occurring, and does it have a predominantly positive or negative impact on families and wider society?\n\nGive reasons for your answer and include any relevant examples from your own knowledge or experience. Write at least 250 words.",
  },
  {
    id: "t2-opinion-aviation-tax",
    taskType: "task2",
    category: "opinion",
    topic: "Climate & Economics",
    title: "Heavy Taxation on Air Travel vs. Clean-Fuel Innovation",
    promptText:
      "Imposing steep environmental taxes on commercial air tickets is the best way to curb aviation emissions.\n\nTo what extent do you agree or disagree with this statement?\n\nGive reasons for your answer and include any relevant examples from your own knowledge or experience. Write at least 250 words.",
  },
  {
    id: "t2-discussion-heritage-architecture",
    taskType: "task2",
    category: "discussion",
    topic: "Architecture & Culture",
    title: "Preserving Historic Buildings vs. High-Density Housing",
    promptText:
      "In rapidly expanding cities facing acute housing shortages, some argue that older historic buildings should be demolished to make room for high-density residential towers, whereas others insist historic districts must be strictly preserved.\n\nDiscuss both these views and give your own opinion.\n\nGive reasons for your answer and include any relevant examples from your own knowledge or experience. Write at least 250 words.",
  },
];

export const STARTER_VAULT_ITEMS = [
  {
    id: "vault-seed-l1-actually",
    itemType: "error",
    criterion: "L1",
    originalText: "Actually, metropolitan rail networks face severe capacity constraints.",
    upgradedText: "Currently, metropolitan rail networks face severe capacity constraints.",
    explanation:
      "Spanish → English false cognate: 'actualmente' means 'currently / at present', whereas English 'actually' means 'in fact / contrary to expectation'.",
    exampleSentence:
      "At present, public subsidies account for nearly forty percent of regional transit revenue.",
  },
  {
    id: "vault-seed-l1-commasplice",
    itemType: "error",
    criterion: "L1",
    originalText:
      "Tuition fees have risen sharply over the past decade, however, enrolment in STEM degrees has remained resilient.",
    upgradedText:
      "Although tuition fees have risen sharply over the past decade, enrolment in STEM degrees has remained resilient.",
    explanation:
      "Comma splice with conjunctive adverb ('however'). In formal English academic prose, join independent clauses with a semicolon ('; however,') or subordinate one clause ('Although...').",
    exampleSentence:
      "Municipal cycling spending peaked in 2016; nevertheless, commuter adoption continued to climb steadily.",
  },
  {
    id: "vault-seed-cc-nominalization",
    itemType: "collocation",
    criterion: "CC",
    originalText: "Secondly, because factories are automated, fewer low-skilled people have jobs.",
    upgradedText:
      "This rapid industrial automation has simultaneously contracted demand for routine manual labour.",
    explanation:
      "Replaces mechanical sequence linkers ('Secondly') with demonstrative nominalisation ('This rapid industrial automation...'), a hallmark of Band 8.0+ Coherence & Cohesion.",
    exampleSentence:
      "Such demographic shifts place considerable fiscal pressure on state pension systems.",
  },
  {
    id: "vault-seed-lr-hedging",
    itemType: "collocation",
    criterion: "LR",
    originalText: "Banning cars will definitely destroy small downtown shops.",
    upgradedText:
      "Unphased vehicle restrictions risk disproportionately eroding footfall for independent retailers.",
    explanation:
      "Academic hedging ('risk disproportionately eroding') avoids sweeping absolutes ('will definitely destroy'), elevating both Task Response nuance and Lexical Resource.",
    exampleSentence:
      "Stringent punitive levies tend to burden lower-income commuters unless paired with reliable transit alternatives.",
  },
];
